param([string]$App = 'D:\BIM-S_TestArtifacts\build\bin\BimSAgent\debug\BimSAgent.dll',
      [string]$ArtifactRoot = 'D:\BIM-S_TestArtifacts\rag-chat-live')
$ErrorActionPreference = 'Stop'
$OutputEncoding = New-Object System.Text.UTF8Encoding $false
$repo = Split-Path $PSScriptRoot -Parent
$run = Join-Path $ArtifactRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$watched = @('history.json','context-state.json','facts.json','tasks/task-1.json','tasks/task-2.json')
$before = @{}
foreach ($relative in $watched) {
    $path = Join-Path $repo $relative
    $before[$relative] = if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path).Hash } else { '' }
}
$scenarios = @(
    @{
        name='stairs'; task=1; family='КркНес_ЛПлощадка_Монолитная'; parameter='_Опоры'; reference='Какие значения у того параметра в этом семействе?';
        messages=@(
            'Цель: подобрать семейство для монолитной лестничной площадки. Какой вариант есть в базе?',
            'Ограничение: рассматриваем только монолитные железобетонные площадки. Какое семейство подходит?',
            'Термин: ЛП в нашем разговоре означает лестничную площадку. Для чего предназначена ЛП?',
            'Выбираю КркНес_ЛПлощадка_Монолитная. Где его размещают?',
            'Под тем параметром дальше понимаем параметр _Опоры. Какие у него варианты?',
            'Решение: сначала сравнить варианты опирания, не менять модель Revit. Какие варианты есть?',
            'Какие значения у того параметра в этом семействе?',
            'Уточнение: рассматриваем опирание двух монолитных маршей. Какой вариант опоры нужен?',
            'Это семейство подходит для нашей исходной цели?',
            '1. Как называется выбранное семейство? 2. Где его размещают? 3. Какие варианты _Опоры описаны?',
            'Ограничение: не использовать Блок_СМР. Что проверять у выбранного семейства?',
            'Какой следующий шаг в рамках нашей цели без изменения модели?',
            'Уточнение: пока нужна только справочная информация. Какие типоразмеры предусмотрены?',
            'Для этого семейства перечисли подтверждённые варианты опор.'
        )
    },
    @{
        name='openings'; task=2; family='ОбщМд_Отверстие_Стена_Круглое'; parameter='диаметр'; reference='Как изменить тот параметр у этого семейства?';
        messages=@(
            'Цель: подобрать круглое и прямоугольное отверстия в стене. Какие семейства есть?',
            'Ограничение: только отверстия в стенах, варианты для перекрытий исключаем. Что подходит?',
            'Термин: КП означает круглое отверстие в стене. Для чего предназначено КП?',
            'Выбираю ОбщМд_Отверстие_Стена_Круглое для круглого варианта. Где его размещают?',
            'Под тем параметром дальше понимаем диаметр отверстия. Как он задаётся?',
            'Решение: сравнить круглый и прямоугольный варианты, ничего не изменяя в Revit. В чём отличие?',
            'Как изменить тот параметр у этого семейства?',
            'Уточнение: стену уже выбрали, пока рассматриваем только круглый вариант. Как размещать его?',
            'Какие ограничения применения есть у этого семейства?',
            '1. Какое семейство выбрано? 2. Для чего оно предназначено? 3. На каком виде с ним работают?',
            'Ограничение: не использовать Блок_СМР. Что ещё проверить перед размещением?',
            'Для нашей исходной цели какой прямоугольный вариант остался для сравнения?'
        )
    }
)
$previous = $env:BIMS_RAG_CHAT_DIRECTORY
$reports = @()
try {
    foreach ($scenario in $scenarios) {
        $env:BIMS_RAG_CHAT_DIRECTORY = Join-Path $run $scenario.name
        $log1 = Join-Path $run ($scenario.name + '-before-restart.log')
        $log2 = Join-Path $run ($scenario.name + '-after-restart.log')
        # Baseline isolates conversation/state from the CPU cross-encoder's latency.
        @($scenario.messages[0..5]) + @('/state','/exit') |
            & dotnet $App rag chat --new --baseline --strategy structural --top-k 3 --task $scenario.task --verbose |
            Set-Content -Encoding UTF8 -LiteralPath $log1
        if ($LASTEXITCODE -ne 0) { throw 'First chat process failed' }
        $id = Get-Content -Raw -Encoding UTF8 (Join-Path $env:BIMS_RAG_CHAT_DIRECTORY 'active.json') | ConvertFrom-Json
        @($scenario.messages[6..($scenario.messages.Count-1)]) + @('/state','/exit') |
            & dotnet $App rag chat --resume $id --baseline --strategy structural --top-k 3 --verbose |
            Set-Content -Encoding UTF8 -LiteralPath $log2
        if ($LASTEXITCODE -ne 0) { throw 'Resumed chat process failed' }
        $session = Get-Content -Raw -Encoding UTF8 (Join-Path $env:BIMS_RAG_CHAT_DIRECTORY ($id + '.json')) | ConvertFrom-Json
        $text = (Get-Content -Raw -Encoding UTF8 $log1) + (Get-Content -Raw -Encoding UTF8 $log2)
        $retrievals = [regex]::Matches($text, 'Поисковый запрос [0-9]+:').Count
        $referenceTurn = @($session.turns | Where-Object { $_.user -eq $scenario.reference })
        $resolvedQuery = @($referenceTurn | ForEach-Object { $_.retrieval_queries }) -join ' '
        $referenceResolved = $referenceTurn.Count -eq 1 -and $resolvedQuery -match [regex]::Escape($scenario.family) -and
            $resolvedQuery -match [regex]::Escape($scenario.parameter)
        $passed = $session.turns.Count -eq $scenario.messages.Count -and $session.task_id -eq $scenario.task -and
            $session.state.goal.value -and $session.state.constraints.Count -gt 0 -and $session.state.terms.Count -gt 0 -and
            $session.state.selections.Count -gt 0 -and $session.state.decisions.Count -gt 0 -and
            $retrievals -eq ($scenario.messages.Count + 2) -and $referenceResolved
        $reports += [pscustomobject]@{scenario=$scenario.name;passed=[bool]$passed;turns=$session.turns.Count;retrievals=$retrievals;task_id=$session.task_id;session_id=$id;reference_resolved=$referenceResolved}
        Write-Output ($reports[-1] | ConvertTo-Json -Compress)
    }
    foreach ($relative in $watched) {
        $path = Join-Path $repo $relative
        $after = if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path).Hash } else { '' }
        if ($after -ne $before[$relative]) { throw "Unrelated state changed: $relative" }
    }
    $reports | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $run 'report.json')
    Write-Output "Artifacts: $run"
    if (@($reports | Where-Object { -not $_.passed }).Count -gt 0) { exit 1 }
} finally { $env:BIMS_RAG_CHAT_DIRECTORY = $previous }
