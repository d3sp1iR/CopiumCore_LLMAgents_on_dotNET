# CopiumCore — LLM-агенты на .NET

Начальный каркас модуля платформы метаграфов. Целевой сценарий и требования описаны в `CopiumCore_TZ.md`; реализация пока находится на этапе E0.

## Требования

- .NET SDK 10 (версия определяется `global.json`)
- Docker — для контейнерного запуска

## Сборка и запуск

Из корня репозитория:

```bash
dotnet restore CopiumCore.slnx
dotnet build CopiumCore.slnx -c Release --no-restore
dotnet run --project modules/llm-agents-dotnet/src/CopiumCore.Api
```

Адрес HTTP отображается при запуске. `GET /health` проверяет работу процесса; `GET /ready` сейчас возвращает 503, так как исполнитель и зависимости ещё не реализованы. Консольный проект пока содержит только заглушку.

Тестовый Graph API с синтетическими данными можно запустить отдельно:

```bash
dotnet run --project modules/llm-agents-dotnet/src/CopiumCore.TestGraphApi
```

`GET /objects/req-17` и `GET /objects/doc-9` возвращают демонстрационные объекты, неизвестный ID — 404. Сервис не проверяет права и предназначен только для изолированного стенда с синтетическими данными. HTTP-адаптер пока не подключён к API агента.

Контейнер из корня репозитория:

```bash
docker build -f modules/llm-agents-dotnet/Dockerfile -t copiumcore-api .
docker run --rm -p 8080:8080 copiumcore-api
```

Описание границ — в [архитектуре](modules/llm-agents-dotnet/docs/architecture.md), фактический ход работ — в [журнале](modules/llm-agents-dotnet/docs/progress.md). Контракты API, тестовый метаграф, MAF, провайдеры и пользовательские сценарии предстоит реализовать.
