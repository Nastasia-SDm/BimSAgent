# Основные команды BimSAgent

## Обычный запуск

```powershell
dotnet run
```

Запускает интерактивный BimSAgent в терминале.

```powershell
dotnet run -- serve
```

Запускает локальный HTTP-сервис.

## RAG

Индексация документа:

```text
rag index "<путь к docx или pdf>"
```

Вопрос по индексу:

```text
rag ask "<вопрос>"
```

Интерактивный RAG-чат:

```text
rag chat --new
```

Внутри RAG-чата доступны `/new`, `/resume <id>`, `/state`, `/task <id>|none`, `/exit`.

## Контекст

```text
strategy
strategy sticky-facts
strategy sliding-window
strategy branching
```

Для branching также используются `checkpoint`, `branch create <name>` и `branch switch <name>`.

## Память

```text
memory optimize
```

Команда запускает отдельную оптимизацию долгосрочной памяти.

## Профили

```text
profile create <name>
profile use <name>
profile show
profile list
profile skip
```

Профиль хранит Style, Constraints и Context пользователя.

## Задачи

Команды `task ...` создают, открывают, продолжают и приостанавливают многоэтапные задачи. Активная задача может передавать свой контекст в обычный диалог и RAG-чат.

## MCP4–MCP6

```text
mcp4-call extract-family-library
mcp5-call compare-family-library V001
mcp6-call recommend-family-unification V001
mcp6-call recommend-family-from-passport V001
```

MCP4 извлекает структурированные данные из Revit-семейств/моделей, MCP5 сравнивает результаты MCP4, MCP6 формирует рекомендации или сопоставляет данные семейства с PDF-паспортом.

## Семейный pipeline

```powershell
dotnet run -- family pipeline --synthetic
dotnet run -- family pipeline --empty
dotnet run -- family pipeline --library
dotnet run -- family pipeline --resume <pipelineId>
```

Для режима `--library` доступен дополнительный флаг `--llm`.
