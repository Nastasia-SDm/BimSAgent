# BimSAgent

Консольный AI-помощник по Autodesk Revit, BIM, Revit API, C#-плагинам и Dynamo.
Требуется .NET 10 SDK. Сторонних пакетов нет.

Из родительской папки:

```powershell
dotnet build .\BimSAgent\BimSAgent.csproj
dotnet run --project .\BimSAgent\BimSAgent.csproj
```

Перед запросом задайте `OPENAI_API_KEY` в окружении процесса через безопасный
механизм вашей среды. Приложение не запрашивает ключ в CLI, не сохраняет его
в файлы и не выводит в журнал. Сборка не требует ключа и не обращается к API.

Модель по умолчанию — `gpt-4.1`; можно переопределить через `OPENAI_MODEL`.
Введите запрос одной строкой; `/exit`, Ctrl+C или конец ввода завершают работу.
Каждый запрос независим: история диалога не передается.
Агент консультирует и генерирует текст, но не подключается к Revit.

Используется [OpenAI Responses API](https://developers.openai.com/api/reference/resources/responses/methods/create),
с инструкциями роли в `instructions` и запросом в `input`.
