# Автоматическое AI review Pull Request

Workflow `.github/workflows/ai-review.yml` проверяет открытые PR при opened,
synchronize, reopened и ready_for_review. Он публикует один комментарий
`BimSAgent AI review`, обновляя его после новых коммитов. Merge, approve/request
changes, правки файлов и запуск проверяемого кода не выполняются.

## Встраивание в BimSAgent

`Review/ReviewCommand.cs` — команда без создания диалогового агента:

```powershell
dotnet run -- review --input snapshot.json --output review.md
```

Для GitHub есть `tools/Review/BimS.Review.csproj`: он компилирует **те же** исходники
`Review/*.cs` и `Rag/*.cs`, без интерактивных команд и зависимости от установленного
MCP4/Revit. Это запускатель существующего RAG, а не второй RAG или отдельный LLM SDK.
Используются `IndexingService`, `JsonIndexStore`, `RagTokenizer`, `HybridRetriever`,
`Bm25Reranker`, `ContextAssembler`, `OpenAiRagClient`/`OllamaRagClient`.
`IndexingService` получил перегрузку для уже извлечённого `ExtractedDocument`;
прежний вход по пути документа сохранён. HTTP-запросы, обработка API-ошибок,
повторы и чтение ключа общие с RAG. Review имеет свой системный prompt,
поскольку RAG-ответы о семействах имеют другой формат и правила доказательств.

Основной OpenAI-ключ — существующая переменная `OPENAI_API_KEY`. Модель по умолчанию
берётся из `RagDefaults.AnswerModel`. Локальный запуск также учитывает
`BIMS_LLM_PROVIDER=ollama`, существующий loopback endpoint и `qwen3:8b`.
На GitHub-hosted runner workflow явно использует OpenAI, поскольку локального
Ollama там нет. Необязательная Actions Variable `BIMS_REVIEW_MODEL` переопределяет
модель только для review. `BIMS_EMBEDDING_MODEL` и `BIMS_EMBEDDING_DIMENSIONS`
сохраняют существующий формат конфигурации; по умолчанию text-embedding-3-small/1536.

Индекс review временный и удаляется после запуска. Рабочие RAG-индексы, /help,
память, история, профили и MCP1–MCP6 не меняются. Git-S-MCP остаётся отдельным
сервером команды ветки. В CI Git читает объекты через ограниченный сборщик данных:
это не новый MCP-клиент и не требует установки локального Git-S-MCP на runner.

## Что получает модель

Сборщик читает diff от merge-base до точного head SHA, список файлов и текст
новой версии (для удалённых файлов — старой). Для больших файлов выбираются
пронумерованные фрагменты вокруг изменённых строк. Переименования представлены
как удаление и добавление. Binary, symlink, секреты и файлы памяти не раскрываются
как содержимое. Пропуски явно перечисляются в ограничениях покрытия.

Для RAG используются README.md, docs/*.md и C#-код **базовой** версии: отдельно
подбирается документация и код, чтобы одна категория не вытеснила другую.
Ограничения: до 50 изменённых файлов, 120000 символов diff, корпус документации
150000 символов и кода 300000, не более 60000 символов одного файла корпуса;
финальный input до 45000 токенов OpenAI или 12000 локально. Большой PR возвращает
диагностику с предложением разделить изменения. Review не обещает полноты при
ограниченном контексте и не утверждает, что запускало тесты.

## GitHub: точная настройка

1. Примите PR с этой реализацией в `main` вручную. Workflow `pull_request_target`
   должен сначала появиться в основной ветке; из PR с его первоначальным
   добавлением привилегированное review не запустится. Workflow `AI review tests`
   проверяет реализацию уже на установочном PR без секретов и права записи.
2. Откройте репозиторий → **Settings → Secrets and variables → Actions → Secrets →
   New repository secret**. Name: **OPENAI_API_KEY**, Secret: ваш API-ключ OpenAI
   с доступом к embeddings и модели ответа. Не добавляйте ключ в файлы/PR.
   Прямой адрес: https://github.com/Nastasia-SDm/BimSAgent/settings/secrets/actions
3. В **Settings → Actions → General** разрешите GitHub Actions и используемые
   стандартные actions. Workflow сам запрашивает `contents: read` и
   `pull-requests: write`. Если политика организации запрещает право записи,
   разрешите workflow публиковать PR-комментарии. Отдельный PAT не нужен:
   `GITHUB_TOKEN` создаётся GitHub автоматически.
4. Создайте ветку `test/ai-review-smoke` от обновлённого `main`, внесите маленькое
   изменение кода, commit/push и откройте Pull Request в `main`.
5. Во вкладке **Actions → BimSAgent AI review** проверьте запуск; в PR должен
   появиться комментарий с SHA, багами, архитектурными замечаниями и рекомендациями
   либо явным сообщением об отсутствии существенных проблем.
6. Отправьте второй commit в ту же ветку: workflow должен запуститься повторно и
   обновить прежний комментарий. Для ручного повтора: **Actions → BimSAgent AI
   review → Run workflow**, ветка `main`, поле `pr_number` = номер открытого PR.

Не делайте эту проверку обязательным условием merge: LLM здесь консультирует.
При отсутствии ключа, HTTP-ошибке или timeout комментарий явно сообщает, что review
не выполнено; это не «проблем не найдено». Обычный дедлайн — 240 секунд,
`BIMS_REVIEW_TIMEOUT_SECONDS` допускает 15–600; шаг CI ограничен 6 минутами,
job — 12 минутами. Новый commit отменяет устаревший job. Перед публикацией
сверяется текущий SHA PR; устаревшее review не публикуется.

`pull_request_target` всегда собирает доверенную базовую версию. Head PR только
читается из Git-объектов, никогда не checkout/build/run; ключ OpenAI доступен
только шагу review. Передача данных — JSON-файл, без интерполяции diff, названий
веток или текста PR в shell. Модель не получает GitHub token или инструменты.

## Проверки

```powershell
dotnet build tools/Review/BimS.Review.csproj
dotnet run --project tools/Review.Tests/BimS.Review.Tests.csproj
node --test .github/scripts/review.test.cjs
dotnet build BimSAgent.csproj
```

Если локальный MCP4 находится не в стандартном `D:\BIM-S-MCP-4`, можно передать
существующий проект без изменения MCP4:

```powershell
dotnet build BimSAgent.csproj "-p:BimsFamilyCoreProject=D:\1. Job\BIM-S-MCP-4\Core\BimS.Family.Core.csproj"
```

Официальные контракты: [GitHub pull_request_target](https://docs.github.com/en/actions/reference/security/securely-using-pull_request_target),
[GitHub PR-комментарии](https://docs.github.com/en/rest/issues/comments),
[OpenAI Responses](https://developers.openai.com/api/docs/guides/text).
