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

Адрес HTTP отображается при запуске. `GET /health` проверяет работу процесса; `GET /ready` возвращает 503, так как production-зависимости ещё не реализованы. Для запуска демонстрационного агента и консоли используйте руководство E1 ниже.

Тестовый Graph API с синтетическими данными можно запустить отдельно:

```bash
dotnet run --project modules/llm-agents-dotnet/src/CopiumCore.TestGraphApi
```

`GET /objects/req-17` и `GET /objects/doc-9` возвращают демонстрационные объекты, неизвестный ID — 404. Сервис не проверяет права и предназначен только для изолированного стенда с синтетическими данными.

Контейнер из корня репозитория:

```bash
docker build -f modules/llm-agents-dotnet/Dockerfile -t copiumcore-api .
docker run --rm -p 8080:8080 copiumcore-api
```

Инструкция по локальному вертикальному срезу — в [руководстве E1](modules/llm-agents-dotnet/docs/e1.md), описание границ — в [архитектуре](modules/llm-agents-dotnet/docs/architecture.md), фактический ход работ — в [журнале](modules/llm-agents-dotnet/docs/progress.md). Полноценные хранение, авторизация и применение изменений ещё не реализованы.
