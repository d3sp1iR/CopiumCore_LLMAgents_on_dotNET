# Журнал работ

Журнал фиксирует реализованные результаты и фактически выполненные проверки. Плановые показатели и заглушки не считаются пройденными приёмочными испытаниями.

## 2026-11-19 — начальный каркас

- Изучено ТЗ, выбрана структура переносимого модуля согласно разделу 18.
- Создана solution с проектами Domain, Application, Infrastructure, API и Console; зависимости направлены к прикладным слоям.
- Добавлены настройки .NET 10, `.gitignore`, описание архитектуры и инструкции запуска.
- API содержит `/health` и временный `/ready` со статусом 503. Консоль пока не выполняет пользовательские команды.
- Добавлены Dockerfile и workflow CI для восстановления, сборки и запуска тестов solution. Фактические результаты CI следует проверять в GitHub Actions; тестовых проектов пока нет.
- FR/AT: каркас для FR-13 и E0; приёмочные испытания ещё не пройдены.
- Проверки: `global.json`, solution и файлы проектов успешно разобраны как JSON/XML. В [GitHub Actions](https://github.com/d3sp1iR/CopiumCore_LLMAgents_on_dotNET/actions/runs/44603527690) для коммита `207a4dd` прошли restore и Release build; команда test завершилась успешно, но тестовых проектов пока нет. SDK `dotnet` локально отсутствует, Docker daemon не ответил за 10 секунд; запуск контейнера не проверен.
- Реальные модель, хранилище и транспорт ещё не подключены. Следующий этап — контракты и вертикальный срез из раздела 18 ТЗ.

## 2026-11-19 — начало вертикального среза

- Добавлен черновик OpenAPI для минимального вертикального среза; маршруты агента, сессии и запуска пока не реализованы. Форматы политик и параметры интеграции требуют уточнения.
- Добавлена доменная модель Run: обязательные идентификаторы, версия агента, дедлайн и переходы состояний из раздела 5 ТЗ.
- Добавлены модульные тесты допустимых и запрещённых переходов и проверки входных данных. Это часть FR-04 и FR-14, но не реализация API, worker или приёмочных испытаний.
- Проверка: [CI для d777c8e](https://github.com/d3sp1iR/CopiumCore_LLMAgents_on_dotNET/actions/runs/44604645072) восстановил зависимости, собрал solution и выполнил 11 тестов успешно. Локальный .NET SDK отсутствует.

## 2026-11-20 — чтение тестового метаграфа

- Создан отдельный тестовый HTTP-сервис с синтетическими объектами `req-17` и `doc-9`, версиями и ответом 404; добавлены HTTP-тесты.
- Добавлены порт чтения и демонстрационный HTTP-адаптер с проверкой ответа и тестами. Адаптер ещё не подключён к API агента или MAF.
- Это подготовка к FR-05, а не приёмка AT-05: данные находятся в памяти, контроль доступа, фрагменты, связи и MetaQuery отсутствуют. Тестовый сервис не предназначен для реальных данных.
- Проверка: [CI для 5f29289](https://github.com/d3sp1iR/CopiumCore_LLMAgents_on_dotNET/actions/runs/44730493502) успешно собрал solution и выполнил 20 тестов: 11 доменных, 6 адаптера и 3 HTTP-теста Graph API. Локальный .NET SDK отсутствует.

## 2026-11-20 — проверка локальной модели для E1

- На локальном Ollama с моделью `qwen3:4b-instruct` выполнен прямой запрос `/api/chat` со схемой инструмента `getObject`. Модель вернула tool call `getObject` с аргументом `objectId: req-17`. Это проверка способности модели выбирать инструмент, а не сквозной тест MAF или E1.
- Оборудование: NVIDIA GeForce RTX 3060, доступно 6 ГиБ VRAM; Linux WSL2; Ollama 0.40.13. SDK устанавливается отдельно для локальных проверок.

## 2026-11-20 — сборка вертикального среза

- Добавлены демонстрационные API агента, сессии, запуска и чтения карточки предложения; данные хранятся в памяти, проверки прав нет. Применение изменений не реализовано.
- Добавлены консольный HTTP-клиент и runtime MAF/MEAI для Ollama с чтением объектов и проверкой кандидата `groupCode` на основе фактически прочитанных данных. Отдельное ребро `supportedBy` отсутствует; локальный fixture использует `decisionId` и обратную ссылку на заявку.
- [CI для 6cc1f9a](https://github.com/d3sp1iR/CopiumCore_LLMAgents_on_dotNET/actions/runs/44732315226) собрал решение и выполнил 22 теста. API-тест предложения использовал тестовый исполнитель; сквозной прогон с настоящей моделью ещё предстоит, поэтому E1 пока не засчитан.
- Запуск демонстрационного профиля описан в `docs/e1.md`.

## 2026-11-20 — сквозная проверка E1

- Локальный прогон Console → API → MAF → Ollama 0.40.13 (`qwen3:4b-instruct`) → Test Graph API завершился `succeeded` / `proposalCreated`, запуск `2a12f1f7c1f04ba2b76376cc83586065`. Консоль показала источники `req-17` версии `7`, `doc-9` версии `3` и предложение `76cda1f0338b4a598c7b73d906fdab5e` со статусом `pendingReview`: `req-17.groupCode` из null в `ИУ5-31Б`.
- Повторное чтение графа подтвердило, что `req-17` остался версии `7` с `groupCode: null`. Предложение не применяется автоматически.
- `dotnet test CopiumCore.slnx -c Release --no-restore` на SDK 10.0.404: 47 тестов прошли, включая 25 проверок валидатора runtime.
- Результат подтверждает демонстрационный вертикальный срез E1, но не полноценную приёмку всех требований ТЗ. Предыдущие попытки вернули `insufficientEvidence` или ошибку; генерация модели нестабильна. В успешном ответе свободный текст ошибочно указал версию «-3», хотя структурированное доказательство содержит `3`. Свободное описание модели не проверяется на достоверность; доверять следует проверенным структурированным полям.
- Данные и предложения существуют только в памяти, авторизация и применение отсутствуют, `/ready` остаётся 503; граф использует поля-ссылки вместо ребра `supportedBy`.

## 2026-11-22 — E2: SQLite review и CAS-срез

- Добавлен опциональный профиль `CopiumCore:UseSqlite`: SQLite хранит proposals, версии решений, idempotency records и outbox events. E1 остаётся in-memory профилем.
- Review API поддерживает `If-Match`, `Idempotency-Key`, approve/reject, повтор решения и конфликт устаревшей версии. Запись proposal в SQLite выполняется до публикации run как `succeeded`, чтобы GET завершённого запуска не видел незаписанную карточку.
- Добавлен application-порт `IChangeWriter` и HTTP-адаптер с `expectedVersion` и устойчивым `operationId`. Test Graph API теперь имеет stateful CAS endpoint `PUT /objects/{id}/attributes/{field}` с защитой от конкурентной версии и повторного эффекта.
- Проверка: CI для [f88a905](https://github.com/d3sp1iR/CopiumCore_LLMAgents_on_dotNET/actions/runs/45068741221) прошёл: restore, Release build и все тесты solution. CI для [5755a9a](https://github.com/d3sp1iR/CopiumCore_LLMAgents_on_dotNET/actions/runs/45069240974) также зелёный; первый CI для `4017f66` остановился на compile fix в вызове reject и исправлен следующим коммитом.
- Добавлен HTTP-тест CAS: успешный переход версии 7→8, replay по тому же `operationId` и конфликт со stale `expectedVersion`. CI для `5974a5a` остановился на отсутствующем `System.Net.Http.Json` в тесте; using добавлен, повторная проверка обязательна.
- Добавлен SQLite-аудит решений в той же транзакции, что и переход proposal и outbox; доступен `GET /proposals/{id}/audit`, API-тест проверяет approve и replay. CI для `bdbb8e1` прошёл: restore, Release build и все тесты solution, включая CAS replay/stale conflict. CI для `5877a88` также зелёный после добавления audit endpoint и проверки.
- Добавлен отдельный адаптер GigaChat через общий `IChatClient`: Ollama остаётся provider по умолчанию, а `CopiumCore:ModelProvider=GigaChat` использует GigaChat OAuth и Chat Completions API. GigaChat требует `GigaChat:Credentials` или `GIGACHAT_CREDENTIALS`; отсутствие credentials не влияет на Ollama.
- Добавлен `tools/at02_gigachat_smoke.py`: без `GIGACHAT_CREDENTIALS` внешний AT-02 возвращает машинно-читаемый статус `skipped` с причиной; при credentials запускает API-путь, где runtime выполняет полный tool-call цикл. Live GigaChat AT-02 не засчитан без credentials и доступного endpoint.
- Добавлен E3-срез provenance/staleness: `GET /proposals/{id}/staleness` перечитывает все evidence через `IGraphReader`, возвращает `SOURCE_VERSION_CHANGED`, `SOURCE_NOT_FOUND` или `SOURCE_UNAVAILABLE`; approve блокируется при устаревшем основании. CI для `6b97b39` прошёл: restore, Release build и все тесты solution.
- Добавлен API-тест с изменённой версией evidence: proposal хранит версию `3`, reader возвращает `4`, endpoint должен объяснить `SOURCE_VERSION_CHANGED`. Проверка выполняется в CI текущего коммита.
- Добавлен `SqliteRunStore`: sessions и runs сохраняются в SQLite, terminal transitions записываются после выполнения, а `GET /runs/{id}` читает persisted state после исчезновения in-memory записи. CI для `0565e2f` прошёл: restore, Release build и все тесты solution.
- Добавлен restart-тест: завершённый run создаётся первым API-хостом и читается вторым хостом с той же SQLite-базой. Проверка выполняется в CI текущего коммита.
- Добавлен cancel-срез: per-run `CancellationTokenSource`, `POST /runs/{id}/cancel`, terminal status `cancelled` сохраняется через `SqliteRunStore`. Добавлен интеграционный тест с блокирующим executor; CI для `80ddd81` прошёл: restore, Release build и все тесты solution.
- Добавлен `OutboxDispatcher` с `IEventSink`, повторным polling и отметкой `published`; SQLite-тест проверяет публикацию `llm.proposal.created` и отсутствие повторного эффекта по `eventId`. CI для `d375c40` прошёл: restore, Release build и все тесты solution.
- Добавлен SQLite provenance index `provenance_index`, который строится из сохранённых `SourceRef` в одной транзакции с proposal/outbox. Endpoint `GET /proposals/by-source/{sourceId}` возвращает зависимые proposal IDs; API-тест проверяет индекс для `doc-9`. CI для `3b84b86` прошёл: restore, Release build и все тесты solution.
- AT-17: скрипт `tools/at17_benchmark.py` создал фиксированный snapshot `seed=17017`, 100 proposal и изменение `doc-042` версии 3→4. Первоначальный A/B/C cost benchmark был симуляцией: B — 400 reads, C — 1 read, B/C нашли один stale proposal `proposal-042`.
- Выполнен реальный локальный AT-17 A через Ollama `qwen3:4b-instruct` на том же snapshot: 100/100 `/api/chat` вызовов успешно вернули tool call `getObject`, ошибок 0, median latency `323.4291 ms`; `docs/at17/report.json` содержит `liveA` и `A_is_simulation=false`. Отдельный smoke также подтвердил `done=true` и аргумент `objectId=req-17`.
- Исправлен порядок approve при подключённом Graph: SQLite CAS резервирует proposal в `applying`, Graph CAS выполняется с устойчивым `operationId`, затем решение фиксируется как `approved`; при Graph conflict reservation освобождается. Review-only профиль без `Graph:Url` сохраняет прямой SQLite CAS. CI для `5be40be` прошёл: restore, Release build и все тесты solution.
- Добавлен timeout/recovery-срез: необязательный `timeoutMs` сохраняется как deadline, истечение переводит run в `timedOut/RUN_TIMEOUT`, пользовательский cancel остаётся `cancelled`; при старте SQLite-профиля queued/running записи переводятся в `failed/RUN_INTERRUPTED`. Добавлен интеграционный timeout-тест. CI для `44feb6b` прошёл restore/build, но тест timeout обнаружил гонку: ранняя отмена происходила до сохранения `running`; сохранение состояния перенесено в защищённый блок. Повторный CI для `b0fb8f8` зелёный: restore, Release build и все тесты solution.
- Добавлен интеграционный recovery-тест: первый API-хост сохраняет `running`, второй при старте переводит запись в `failed/RUN_INTERRUPTED`. CI для [b1e9d77](https://github.com/d3sp1iR/CopiumCore_LLMAgents_on_dotNET/actions/runs/45073051135) зелёный: restore, Release build и все тесты solution.
- Добавлен API integration test конкурентных approve/reject через реальный Test Graph API и общий SQLite store: ровно один decision выигрывает, второй получает conflict; Graph изменяется только при approved и получает одну версию 7→8. CI для [ec2f73e](https://github.com/d3sp1iR/CopiumCore_LLMAgents_on_dotNET/actions/runs/45073479354) зелёный: restore, Release build и все тесты solution.
- Локальный Ollama smoke выполнен после запуска daemon: `/api/tags` показал `qwen3:4b-instruct` с capability `tools`; прямой `/api/chat` smoke вернул `done=true` и `getObject(objectId=req-17)`. OpenAI больше не является частью локального E2/E3 профиля. После удаления OpenAI dependency и добавления `--live-a` CI для [4444750](https://github.com/d3sp1iR/CopiumCore_LLMAgents_on_dotNET/actions/runs/45084235255) зелёный: restore, Release build и все тесты solution.
- Ограничения: восстановление не повторяет неопределённый running execution и не создаёт поздних proposals.
