# Каталог событий и содержимого Data

Каталог отражает текущую реализацию разбора LGP/LGF, проверенную на платформе
**8.3.27.2325**. В таблице 151 код из контрольного каталога названий: 146 получают
русское представление штатной выгрузки, ещё 5 сохраняют исходный код. Это не
исчерпывающий список событий всех версий платформы. Специальный разбор сложного
`Data` реализован для **50 кодов**, в пределах перечисленных форм.

Колонка `Data` в ClickHouse остаётся `String`. Ниже перечислены ключи JSON внутри
этой строки. Числа и логические значения сохраняются **JSON-строками** (`"15"`,
`"true"`), даты — строками `yyyy-MM-ddTHH:mm:ss` без назначения часового пояса.
Исключения с `null`, массивами и объектами отмечены отдельно. `P/число` обозначает
маркер формы внутри данных ЖР, а не поле результирующего события.

## Форматы результата

| Формат | Содержимое |
|---|---|
| Обычный JSON | Объект с указанными в каталоге полями, например `{"Name":"Пользователь"}`. |
| Позиционный JSON | `{"$format":"1c-positional-data-v1","layout":"P/65535","values":{...},"unnamedFields":[],"nodes":[...]}`. Поля перечислены в `values`; полное дерево сохранено в `nodes`. У поддержанных форм все позиции именованы. |
| Именованный JSON | `{"$format":"1c-named-data-v1","values":{...},"nodes":[...]}`. Имена взяты непосредственно из записи ЖР. |
| Общее дерево | `{"$format":"1c-brackets-v1","nodes":[...]}`. Структура сохранена, назначения полей не предполагаются. |
| Скаляр | Строковое представление значения без JSON-конверта: строка, число, дата, логическое значение, ссылка либо пустая строка для `U`. |

Специальный разбор требует совпадения кода события, маркера, количества и типов
полей. Другой вариант того же события проходит [общий разбор](#generic).
Пустой `P` не заменяется вымышленными пустыми значениями известных полей.
Распознавание русского названия само по себе не означает наличие специального
разбора `Data`.

## События

Для строк «Общий разбор» отдельные назначения позиционных полей не определены;
скаляры, именованные пары и дерево поддерживаются по общим правилам.
Пять строк с исходным кодом вместо названия включают контрольные неизвестные
варианты; в `_$User$_.AuthenticationUnlockError ` сохранён конечный пробел —
это другой код, не совпадающий с известным `_$User$_.AuthenticationUnlockError`.

| Код события | Название в Event | Содержимое Data для поддержанной формы |
|---|---|---|
| `_$Access$_.Access` | Доступ. Доступ | [Таблица](#table-data) в `Data`: `P/1` + `Z`; обычный JSON |
| `_$Access$_.AccessDenied` | Доступ. Отказ в доступе | `Right`: `P/1` + строка; либо `Action`, `Data: null`: `P/6` + строка и `U`; обычный JSON |
| `_$Data$_.DataHistorySettingsUpdate` | Данные. Изменение настроек истории данных | [Таблица](#table-data) в `Data`: `P/1`, именованное `Данные` с типом `Z`; обычный JSON |
| `_$Data$_.Delete` | Данные. Удаление | [Общий разбор](#generic) |
| `_$Data$_.DeletePredefinedData` | Данные. Удаление предопределенных данных | [Общий разбор](#generic) |
| `_$Data$_.DeleteVersions` | Данные. Удаление версий | [Общий разбор](#generic) |
| `_$Data$_.New` | Данные. Добавление | [Общий разбор](#generic) |
| `_$Data$_.NewPredefinedData` | Данные. Добавление предопределенных данных | [Общий разбор](#generic) |
| `_$Data$_.NewVersion` | Данные. Добавление версии | [Общий разбор](#generic) |
| `_$Data$_.Pos` | `_$Data$_.Pos` | [Общий разбор](#generic) |
| `_$Data$_.Post` | Данные. Проведение | [Общий разбор](#generic) |
| `_$Data$_.PredefinedDataInitialization` | Данные. Инициализация предопределенных данных | [Таблица](#table-data) в `Data`: `P/1`, именованное `Данные` или `Ссылка` с типом `Z`; обычный JSON |
| `_$Data$_.PredefinedDataInitializationDataNotFound` | Данные. Инициализация предопределенных данных, данные не найдены | [Общий разбор](#generic) |
| `_$Data$_.SetPredefinedDataInitialization` | Данные. Установка инициализации предопределенных данных | [Общий разбор](#generic) |
| `_$Data$_.SetStandardODataInterfaceContent` | Данные. Изменение состава стандартного интерфейса OData | [Общий разбор](#generic) |
| `_$Data$_.TotalsMaxPeriodUpdate` | Данные. Изменение максимального периода рассчитанных итогов | [Общий разбор](#generic) |
| `_$Data$_.TotalsMinPeriodUpdate` | Данные. Изменение минимального периода рассчитанных итогов | [Общий разбор](#generic) |
| `_$Data$_.Unpost` | Данные. Отмена проведения | [Общий разбор](#generic) |
| `_$Data$_.Update` | Данные. Изменение | [Общий разбор](#generic) |
| `_$Data$_.UpdatePredefinedData` | Данные. Изменение предопределенных данных | [Таблица](#table-data) в `Data`: `P/1`, именованное `Данные` или `Ссылка` с типом `Z`; обычный JSON |
| `_$Data$_.VersionCommentUpdate` | `_$Data$_.VersionCommentUpdate` | [Общий разбор](#generic) |
| `_$Debug$_.DebugTargetAttach` | Отладка. Подключение предмета отладки | [Общий разбор](#generic) |
| `_$Debug$_.DebugTargetDetach` | Отладка. Отключение предмета отладки | [Общий разбор](#generic) |
| `_$Debug$_.ExpressionEvaluation` | Отладка. Вычисление выражения | [Общий разбор](#generic) |
| `_$InfoBase$_.AdditionalAuthenticationSettingsUpdate` | Информационная база. Изменение дополнительных настроек аутентификации | [Дополнительные настройки аутентификации](#authentication-settings): 45 одиночных масок и `P/2564`; обычный JSON |
| `_$InfoBase$_.AdditionalAuthenticationSettingsUpdateError` | Информационная база. Ошибка изменения дополнительных настроек аутентификации | [Общий разбор](#generic) |
| `_$InfoBase$_.AdministrationParametersChange` | Информационная база. Изменение административных параметров | [Общий разбор](#generic) |
| `_$InfoBase$_.ConfigExtensionUpdate` | Информационная база. Изменение расширения конфигурации | [Расширение](#extension): 15 значений в `P` без маски; обычный JSON |
| `_$InfoBase$_.ConfigUpdate` | Информационная база. Изменение конфигурации | `Vendor`, `Version`: две строки в `P` без маски; обычный JSON |
| `_$InfoBase$_.ConfigUpdateError` | Информационная база. Ошибка изменения конфигурации | `Vendor`, `Version`: две строки в `P` без маски; обычный JSON |
| `_$InfoBase$_.DBConfigBackgroundUpdateCancel` | Информационная база. Отмена фонового обновления конфигурации базы данных | [Общий разбор](#generic) |
| `_$InfoBase$_.DBConfigBackgroundUpdateError` | Информационная база. Ошибка фонового обновления конфигурации базы данных | [Общий разбор](#generic) |
| `_$InfoBase$_.DBConfigBackgroundUpdateFinish` | Информационная база. Завершение фонового обновления конфигурации базы данных | [Общий разбор](#generic) |
| `_$InfoBase$_.DBConfigBackgroundUpdateResume` | Информационная база. Продолжение фонового обновления конфигурации базы данных | [Общий разбор](#generic) |
| `_$InfoBase$_.DBConfigBackgroundUpdateStart` | Информационная база. Запуск фонового обновления конфигурации базы данных | [Общий разбор](#generic) |
| `_$InfoBase$_.DBConfigBackgroundUpdateSuspend` | Информационная база. Приостановка фонового обновления конфигурации базы данных | [Общий разбор](#generic) |
| `_$InfoBase$_.DBConfigExtensionDelete` | Информационная база. Удаление расширения конфигурации базы данных | [Расширение](#extension): 15 значений в `P` без маски; обычный JSON |
| `_$InfoBase$_.DBConfigExtensionDeleteError` | Информационная база. Ошибка удаления расширения конфигурации базы данных | [Общий разбор](#generic) |
| `_$InfoBase$_.DBConfigExtensionInsert` | Информационная база. Добавление расширения конфигурации базы данных | [Общий разбор](#generic) |
| `_$InfoBase$_.DBConfigExtensionInsertError` | Информационная база. Ошибка добавления расширения конфигурации базы данных | [Общий разбор](#generic) |
| `_$InfoBase$_.DBConfigExtensionUpdate` | Информационная база. Изменение расширения конфигурации базы данных | [Расширение](#extension): 15 значений в `P` без маски; обычный JSON |
| `_$InfoBase$_.DBConfigExtensionUpdateError` | Информационная база. Ошибка изменения расширения конфигурации базы данных | [Расширение](#extension): 15 значений в `P` без маски; обычный JSON |
| `_$InfoBase$_.DBConfigUpdate` | Информационная база. Изменение конфигурации базы данных | `Vendor`, `Version`: две строки в `P` без маски; обычный JSON |
| `_$InfoBase$_.DBConfigUpdateError` | Информационная база. Ошибка изменения конфигурации базы данных | `Vendor`, `Version`: две строки в `P` без маски; обычный JSON |
| `_$InfoBase$_.DBConfigUpdateStart` | Информационная база. Запуск обновления конфигурации базы данных | [Общий разбор](#generic) |
| `_$InfoBase$_.DumpError` | Информационная база. Ошибка выгрузки в файл | [Общий разбор](#generic) |
| `_$InfoBase$_.DumpFinish` | Информационная база. Окончание выгрузки в файл | [Общий разбор](#generic) |
| `_$InfoBase$_.DumpStart` | Информационная база. Начало выгрузки в файл | [Общий разбор](#generic) |
| `_$InfoBase$_.EraseData` | Информационная база. Удаление данных информационной базы | [Общий разбор](#generic) |
| `_$InfoBase$_.EraseDataError` | Информационная база. Ошибка удаление данных информационной базы | [Общий разбор](#generic) |
| `_$InfoBase$_.EventLogReduce` | Информационная база. Сокращение журнала регистрации | `Date`: единственная дата `D` в `P` без маски; обычный JSON |
| `_$InfoBase$_.EventLogReduceError` | Информационная база. Ошибка сокращения журнала регистрации | `Date`: та же форма, алиас ошибки; обычный JSON |
| `_$InfoBase$_.EventLogSettingsUpdate` | Информационная база. Изменение настроек журнала регистрации | [Настройки ЖР](#event-log-settings): `P/1` и `P/24`; обычный JSON |
| `_$InfoBase$_.EventLogSettingsUpdateError` | Информационная база. Ошибка изменения настроек журнала регистрации | [Настройки ЖР](#event-log-settings): те же проверенные формы, алиас ошибки; обычный JSON |
| `_$InfoBase$_.ExclusiveModeChange` | Информационная база. Изменение режима монопольного доступа к текущей области | `ExclusiveMode`: `P/2`, логическое значение; обычный JSON |
| `_$InfoBase$_.ExclusiveModeChangeError` | Информационная база. Ошибка изменения режима монопольного доступа к текущей области | `ExclusiveMode`: `P/2`, логическое значение; обычный JSON |
| `_$InfoBase$_.InfoBaseAdmParamsUpdate` | Информационная база. Изменение параметров информационной базы | [Общий разбор](#generic) |
| `_$InfoBase$_.InfoBaseAdmParamsUpdateError` | Информационная база. Ошибка изменения параметров информационной базы | [Общий разбор](#generic) |
| `_$InfoBase$_.IntegrationServiceActiveUpdate` | Информационная база. Изменение активности сервиса интеграции | [Общий разбор](#generic) |
| `_$InfoBase$_.IntegrationServiceSettingsUpdate` | Информационная база. Изменение настроек сервиса интеграции | [Общий разбор](#generic) |
| `_$InfoBase$_.MasterNodeUpdate` | Информационная база. Изменение главного узла | [Общий разбор](#generic) |
| `_$InfoBase$_.ParametersUpdate` | Информационная база. Изменение параметров информационной базы | [Параметры базы](#parameters): 21 проверенная маска; позиционный JSON |
| `_$InfoBase$_.ParametersUpdateError` | Информационная база. Ошибка изменения параметров информационной базы | [Параметры базы](#parameters): 21 проверенная маска; позиционный JSON |
| `_$InfoBase$_.PasswordCompromiseCheckListUpdate` | Информационная база. Изменение списка проверки раскрытия пароля | [Общий разбор](#generic) |
| `_$InfoBase$_.PasswordCompromiseCheckListUpdateError` | Информационная база. Ошибка изменения списка проверки раскрытия пароля | [Общий разбор](#generic) |
| `_$InfoBase$_.PasswordCompromiseCheckServiceError` | Информационная база. Ошибка сервиса проверки раскрытия пароля | [Общий разбор](#generic) |
| `_$InfoBase$_.PredefinedDataUpdate` | Информационная база. Обновление предопределенных данных | [Таблица](#table-data) в `Data`: `P/1`, именованное `Данные` или `Ссылка` с типом `Z`; обычный JSON |
| `_$InfoBase$_.RegionalSettingsChange` | Информационная база. Изменение региональных установок | [Региональные настройки](#regional): 11 значений в `P` без маски; обычный JSON |
| `_$InfoBase$_.RegionalSettingsChangeError` | Информационная база. Ошибка изменения региональных установок | [Общий разбор](#generic) |
| `_$InfoBase$_.RegionalSettingsUpdate` | Информационная база. Изменение региональных установок | [Общий разбор](#generic) |
| `_$InfoBase$_.RegionalSettingsUpdateError` | `_$InfoBase$_.RegionalSettingsUpdateError` | [Общий разбор](#generic) |
| `_$InfoBase$_.RestoreError` | Информационная база. Ошибка загрузки из файла | [Общий разбор](#generic) |
| `_$InfoBase$_.RestoreFinish` | Информационная база. Окончание загрузки из файла | [Общий разбор](#generic) |
| `_$InfoBase$_.RestoreStart` | Информационная база. Начало загрузки из файла | [Общий разбор](#generic) |
| `_$InfoBase$_.RoleUpdate` | Информационная база. Изменение роли | [Общий разбор](#generic) |
| `_$InfoBase$_.SecondAuthenticationFactorTemplateDelete` | Информационная база. Удаление шаблона второго фактора аутентификации | `Name`: именованное `Имя` в `P/1`; обычный JSON |
| `_$InfoBase$_.SecondAuthenticationFactorTemplateNew` | Информационная база. Добавление шаблона второго фактора аутентификации | `Name`: именованное `Имя` в `P/1`; обычный JSON |
| `_$InfoBase$_.SecondAuthenticationFactorTemplateUpdate` | Информационная база. Изменение шаблона второго фактора аутентификации | `Name`: именованное `Имя` в `P/1`; обычный JSON |
| `_$InfoBase$_.SecondFactorAuthTemplateDelete` | Информационная база. Удаление шаблона второго фактора аутентификации | [Общий разбор](#generic) |
| `_$InfoBase$_.SecondFactorAuthTemplateNew` | Информационная база. Добавление шаблона второго фактора аутентификации | [Общий разбор](#generic) |
| `_$InfoBase$_.SecondFactorAuthTemplateUpdate` | Информационная база. Изменение шаблона второго фактора аутентификации | [Общий разбор](#generic) |
| `_$InfoBase$_.SecurityProfileChange` | Информационная база. Изменение профиля безопасности | [Общий разбор](#generic) |
| `_$InfoBase$_.SessionLockChange` | Информационная база. Изменение блокировки начала сеансов | `Use`, `Begin`, `End`, `Message`, `KeyCode`, `Parameter`: `B,D,D,S,S,S` в `P`; обычный JSON |
| `_$InfoBase$_.SessionLockChangeError` | Информационная база. Ошибка изменения блокировки начала сеансов | `Use`, `Begin`, `End`, `Message`, `KeyCode`, `Parameter`: `B,D,D,S,S,S` в `P`; обычный JSON |
| `_$InfoBase$_.SetPredefinedDataUpdate` | Информационная база. Установка обновления предопределенных данных | [Общий разбор](#generic) |
| `_$InfoBase$_.TARImportant` | Тестирование и исправление. Ошибка | [Общий разбор](#generic) |
| `_$InfoBase$_.TARInfo` | Тестирование и исправление. Сообщение | [Общий разбор](#generic) |
| `_$InfoBase$_.TARMess` | Тестирование и исправление. Предупреждение | [Общий разбор](#generic) |
| `_$InfoBase$_.UserPasswordPolicyDelete` | Информационная база. Удаление политики паролей пользователей | `Name`: единственная строка в `P`; обычный JSON |
| `_$InfoBase$_.UserPasswordPolicyDeleteError` | Информационная база. Ошибка удаления политики паролей пользователей | [Общий разбор](#generic) |
| `_$InfoBase$_.UserPasswordPolicyNew` | Информационная база. Добавление политики паролей пользователей | [Политика паролей](#policy): `P/255`, 9 полей; позиционный JSON |
| `_$InfoBase$_.UserPasswordPolicyNewError` | Информационная база. Ошибка добавления политики паролей пользователей | [Общий разбор](#generic) |
| `_$InfoBase$_.UserPasswordPolicyUpdate` | Информационная база. Изменение политики паролей пользователей | [Политика паролей](#policy): `P/255`, 9 полей; позиционный JSON |
| `_$InfoBase$_.UserPasswordPolicyUpdateError` | Информационная база. Ошибка изменения политики паролей пользователей | [Общий разбор](#generic) |
| `_$InfoBase$_.VerifyAndRepairImportant` | Тестирование и исправление. Ошибка | [Общий разбор](#generic) |
| `_$InfoBase$_.VerifyAndRepairInfo` | Тестирование и исправление. Сообщение | [Общий разбор](#generic) |
| `_$InfoBase$_.VerifyAndRepairMessage` | Тестирование и исправление. Предупреждение | [Общий разбор](#generic) |
| `_$Job$_.Cancel` | Фоновое задание. Отмена | [Общий разбор](#generic) |
| `_$Job$_.Error` | Фоновое задание. Ошибка выполнения | [Общий разбор](#generic) |
| `_$Job$_.Fail` | Фоновое задание. Ошибка выполнения | [Общий разбор](#generic) |
| `_$Job$_.Finish` | Фоновое задание. Успешное завершение | [Общий разбор](#generic) |
| `_$Job$_.Start` | Фоновое задание. Запуск | [Общий разбор](#generic) |
| `_$Job$_.Succeed` | Фоновое задание. Успешное завершение | [Общий разбор](#generic) |
| `_$Job$_.Terminate` | Фоновое задание. Принудительное завершение | [Общий разбор](#generic) |
| `_$OpenIDProvider$_.NegativeAssertion` | Провайдер OpenID. Отклонено | [Общий разбор](#generic) |
| `_$OpenIDProvider$_.PositiveAssertion` | Провайдер OpenID. Подтверждено | [Общий разбор](#generic) |
| `_$PerformError$_` | Ошибка выполнения | [Общий разбор](#generic) |
| `_$Session$_.AddInAttach` | Сеанс. Подключение внешней компоненты | `Name`, `Location`, `Type: null`, `AttachmentType: null`: `S,S,U,U` в `P`; обычный JSON |
| `_$Session$_.AddInAttachError` | Сеанс. Ошибка подключения внешней компоненты | `Name`, `Location`, `Type: null`, `AttachmentType: null`: `S,S,U,U` в `P`; обычный JSON |
| `_$Session$_.Authentication` | Сеанс. Аутентификация | [Аутентификация](#authentication): `P/513`, `P/514`, `P/518`; обычный JSON |
| `_$Session$_.AuthenticationError` | Сеанс. Ошибка аутентификации | [Аутентификация](#authentication): `P/513`, `P/514`, `P/518`; обычный JSON |
| `_$Session$_.AuthenticationFirstFactor` | Сеанс. Аутентификация первый фактор | [Общий разбор](#generic) |
| `_$Session$_.ConfigExtensionApplyError` | Сеанс. Ошибка применения расширения конфигурации | `Name`: единственная строка в `P`; обычный JSON |
| `_$Session$_.DataZoneChange` | Сеанс. Изменение текущей области | [Общий разбор](#generic) |
| `_$Session$_.DataZoneChangeError` | Сеанс. Ошибка изменения текущей области | [Общий разбор](#generic) |
| `_$Session$_.ExternalDataProcessorConnect` | Сеанс. Подключение внешней обработки | `Path`, `Name`, `SafeMode`, `SecurityProfile`, `UnsafeOperationProtection`, `LanguageCode`: `S,S,B,S,B,S` в `P`; обычный JSON |
| `_$Session$_.ExternalDataProcessorConnectError` | Сеанс. Ошибка подключения внешней обработки | `Path`, `Name`, `SafeMode`, `SecurityProfile`, `UnsafeOperationProtection`, `LanguageCode`: `S,S,B,S,B,S` в `P`; обычный JSON |
| `_$Session$_.ExternalReportConnect` | Сеанс. Подключение внешнего отчета | `Path`, `Name`, `SafeMode`, `SecurityProfile`, `UnsafeOperationProtection`, `LanguageCode`: `S,S,B,S,B,S` в `P`; обычный JSON |
| `_$Session$_.ExternalReportConnectError` | Сеанс. Ошибка подключения внешнего отчета | `Path`, `Name`, `SafeMode`, `SecurityProfile`, `UnsafeOperationProtection`, `LanguageCode`: `S,S,B,S,B,S` в `P`; обычный JSON |
| `_$Session$_.Finish` | Сеанс. Завершение | [Общий разбор](#generic) |
| `_$Session$_.PasswordChangeError` | Сеанс. Ошибка смены пароля | [Общий разбор](#generic) |
| `_$Session$_.PasswordCompromiseCheckServiceErrorOnAuthentication` | Сеанс. Ошибка сервиса проверки раскрытия пароля при аутентификации | [Общий разбор](#generic) |
| `_$Session$_.PasswordRecoveryAttempt` | Сеанс. Попытка восстановления пароля | [Общий разбор](#generic) |
| `_$Session$_.PasswordRecoveryAttemptError` | Сеанс. Ошибка попытки восстановления пароля | [Общий разбор](#generic) |
| `_$Session$_.PasswordRequirementsViolationOnAuthentication` | Сеанс. Несоответствие пароля требованиям при аутентификации | `Name`: именованное `Имя` в `P/1`; обычный JSON |
| `_$Session$_.SecurityProfileRestrictionAccessRightsExtension` | Сеанс. Ограничение профиля безопасности: Расширение прав доступа | [Общий разбор](#generic) |
| `_$Session$_.SecurityProfileRestrictionCOMObjectAccess` | Сеанс. Ограничение профиля безопасности: Доступ к COM-объекту | [Общий разбор](#generic) |
| `_$Session$_.SecurityProfileRestrictionCryptographyOperations` | Сеанс. Ограничение профиля безопасности: Использование функций криптографии | [Общий разбор](#generic) |
| `_$Session$_.SecurityProfileRestrictionExternalComponentAccess` | Сеанс. Ограничение профиля безопасности: Доступ к внешней компоненте 1С | [Общий разбор](#generic) |
| `_$Session$_.SecurityProfileRestrictionExternalModuleAccess` | Сеанс. Ограничение профиля безопасности: Запуск внешнего модуля | [Общий разбор](#generic) |
| `_$Session$_.SecurityProfileRestrictionFileSystemAccess` | Сеанс. Ограничение профиля безопасности: Доступ к файловой системе сервера | [Общий разбор](#generic) |
| `_$Session$_.SecurityProfileRestrictionInternetResourceAccess` | Сеанс. Ограничение профиля безопасности: Обращение к ресурсу Интернета | [Общий разбор](#generic) |
| `_$Session$_.SecurityProfileRestrictionModuleExtension` | Сеанс. Ограничение профиля безопасности: Расширение модуля | [Общий разбор](#generic) |
| `_$Session$_.SecurityProfileRestrictionOSApplicationStart` | Сеанс. Ограничение профиля безопасности: Запуск приложения ОС | [Общий разбор](#generic) |
| `_$Session$_.SecurityProfileRestrictionPrivilegedMode` | Сеанс. Ограничение профиля безопасности: Установка привилегированного режима | [Общий разбор](#generic) |
| `_$Session$_.Start` | Сеанс. Начало | [Общий разбор](#generic) |
| `_$Transaction$_.Begin` | Транзакция. Начало | [Общий разбор](#generic) |
| `_$Transaction$_.Commit` | Транзакция. Фиксация | [Общий разбор](#generic) |
| `_$Transaction$_.Rollback` | Транзакция. Отмена | [Общий разбор](#generic) |
| `_$User$_.AuthenticationLock` | Пользователи. Блокировка аутентификации | `Name`: именованное `Имя` в `P/1`; обычный JSON |
| `_$User$_.AuthenticationUnlock` | Пользователи. Разблокировка аутентификации | `Name`: именованное `Имя` в `P/1`; обычный JSON |
| `_$User$_.AuthenticationUnlockError` | Пользователи. Ошибка разблокировки аутентификации | `Name`: именованное `Имя` в `P/1`; обычный JSON |
| `_$User$_.AuthenticationUnlockError ` | `_$User$_.AuthenticationUnlockError ` | [Общий разбор](#generic) |
| `_$User$_.Delete` | Пользователи. Удаление | `Name`: единственная строка в `P`; обычный JSON |
| `_$User$_.DeleteError` | Пользователи. Ошибка удаления | [Общий разбор](#generic) |
| `_$User$_.New` | Пользователи. Добавление | [Пользователь](#user): `P/65535` (28 полей), `P/65534` (27); позиционный JSON |
| `_$User$_.NewError` | Пользователи. Ошибка добавления | [Пользователь](#user): `P/65535` (28 полей), `P/65534` (27); позиционный JSON |
| `_$User$_.PermissionChange` | Пользователи. Изменение прав доступа | [Общий разбор](#generic) |
| `_$User$_.SecurityProfileChange` | `_$User$_.SecurityProfileChange` | [Общий разбор](#generic) |
| `_$User$_.Update` | Пользователи. Изменение | [Пользователь](#user): `P/65535` (28 полей), `P/65534` (27); позиционный JSON |
| `_$User$_.UpdateError` | Пользователи. Ошибка изменения | [Пользователь](#user): `P/65535` (28 полей), `P/65534` (27); позиционный JSON |

<a id="authentication"></a>
## Аутентификация

| Форма | Поля обычного JSON |
|---|---|
| `P/513` | `OSUser`, `AuthenticationMethod` |
| `P/514` | `Name`, `AuthenticationMethod` |
| `P/518` | `Name`, `CurrentOSUser`, `AuthenticationMethod` |

`AuthenticationMethod` сохраняет числовой код строкой. Другие варианты
аутентификации не считаются позиционно разобранными на основании одного кода
события. Справка называет поле `AuthenticationMode`; существующий ключ JSON
сохранён для совместимости.

<a id="authentication-settings"></a>
## Дополнительные настройки аутентификации

Обычный JSON для `AdditionalAuthenticationSettingsUpdate`. Подтверждены
перечисленные одиночные маски и одна составная. Маски хранятся строками и не
ограничены 32 битами. Нулевая маска `P/{0}` остаётся общим деревом: значений для
восстановления полей в ней нет.

| Маска | Ключ обычного JSON | Тип |
|---|---|---|
| `1` | `PasswordRecoveryMethod` | Перечисление, см. ниже |
| `2` | `PasswordRecoveryURL` | Строка |
| `4` | `VerificationCodeLength` | Число |
| `8` | `MaxUnsuccessfulVerificationCodeValidationAttemptsCount` | Число |
| `16` | `SMTPServerAddress` | Строка |
| `32` | `SMTPUser` | Строка |
| `64` | `SMTPPasswordChanged` | Логическое |
| `128` | `SMTPPort` | Число |
| `256` | `SenderName` | Строка |
| `512` | `Header` | Строка |
| `1024` | `HTMLMessageText` | Строка |
| `2048` | `ShowHelpHyperlink` | Логическое |
| `4096` | `HelpURL` | Строка |
| `8192` | `VerificationCodeRefreshRequestLockDuration` | Число |
| `16384` | `UseSSL` | Логическое |
| `32768` | `AllowAutoSaveAuthentication` | Логическое |
| `65536` | `AutoSaveAuthenticationByDefault` | Логическое |
| `131072` | `AutoSavedAuthenticationLifeTime` | Число |
| `262144` | `UseStandardPasswordCompromiseCheckList` | Логическое |
| `524288` | `UseSpecifiedPasswordCompromiseCheckList` | Логическое |
| `1048576` | `UsePasswordCompromiseCheckService` | Логическое |
| `2097152` | `PasswordCompromiseCheckServiceURL` | Строка |
| `4194304` | `PasswordCompromiseCheckServiceRequestTimeout` | Число |
| `8388608` | `IgnorePasswordCompromiseCheckServiceErrors` | Логическое |
| `16777216` | `VerificationCodeEffectivePeriod` | Число |
| `33554432` | `EmailAuthenticationMethod` | Перечисление, см. ниже |
| `67108864` | `EmailAuthenticationVerificationCodeLength` | Число |
| `134217728` | `EmailAuthenticationMaxUnsuccessfulVerificationCodeValidationAttemptsCount` | Число |
| `268435456` | `EmailAuthenticationSMTPServerAddress` | Строка |
| `536870912` | `EmailAuthenticationSMTPUser` | Строка |
| `1073741824` | `EmailAuthenticationSMTPPasswordChanged` | Логическое |
| `2147483648` | `EmailAuthenticationSMTPPort` | Число |
| `4294967296` | `EmailAuthenticationSenderName` | Строка |
| `8589934592` | `EmailAuthenticationHeader` | Строка |
| `17179869184` | `EmailAuthenticationHTMLMessageText` | Строка |
| `34359738368` | `EmailAuthenticationVerificationCodeRefreshRequestLockDuration` | Число |
| `68719476736` | `EmailAuthenticationUseSSLWhenSendingVerificationCode` | Логическое |
| `137438953472` | `EmailAuthenticationVerificationCodeEffectivePeriod` | Число |
| `549755813888` | `PasswordRecoverySMTPSecureAuthenticationOnly` | Логическое |
| `1099511627776` | `PasswordRecoveryTokenAuthenticationWhenSendingVerificationCode` | Перечисление, см. ниже |
| `2199023255552` | `PasswordRecoveryAccessTokenChanged` | Логическое |
| `8796093022208` | `EmailAuthenticationTokenAuthenticationWhenSendingVerificationCode` | Перечисление, см. ниже |
| `17592186044416` | `EmailAuthenticationAccessTokenChanged` | Логическое |
| `35184372088832` | `PasswordRecoveryFrom` | Строка |
| `70368744177664` | `EmailAuthenticationFrom` | Строка |
| `2564` | `VerificationCodeLength`, `Header`, `ShowHelpHyperlink` | Число, строка, логическое; порядок подтверждён совместным изменением |

| Поле | Проверенные коды `N` и результирующие строки |
|---|---|
| `PasswordRecoveryMethod` | `0` → `Переход по навигационной ссылке`; `1` → `Отправка кода подтверждения через стандартный сервис`; `2` → `Отправка кода подтверждения по заданным параметрам учетной записи`; `3` → `Нет` |
| `EmailAuthenticationMethod` | `1`, `2`, `3` с теми же представлениями отправки и `Нет`; код `0` не интерпретируется |
| `PasswordRecoveryTokenAuthenticationWhenSendingVerificationCode`, `EmailAuthenticationTokenAuthenticationWhenSendingVerificationCode` | `0` → `Не использовать`; `1` → `Использовать`; `2` → `Авто` |

Для этих новых JSON-полей используются фактические ключи и представления
штатной выгрузки. Например, `HTMLMessageText` и
`UseSpecifiedPasswordCompromiseCheckList` отличаются от английских имён,
указанных на странице описания событий в справке. Поля `SMTPPasswordChanged`,
`PasswordRecoveryAccessTokenChanged` и соответствующие поля электронной почты —
логические признаки изменения; пароль или токен из них не восстанавливаются.
Другие комбинации масок, неизвестные коды перечислений и событие
`AdditionalAuthenticationSettingsUpdateError` сохраняются общим разбором.

<a id="event-log-settings"></a>
## Настройки журнала регистрации

Обычный JSON для `EventLogSettingsUpdate` и документированного алиаса
`EventLogSettingsUpdateError`.

| Форма | Поля и значения |
|---|---|
| `P/24`: строка и логическое | `EventName`, `EventIsLogged` — настройка регистрации отдельного события |
| `P/1` с `N/0` | `Levels: "Примечание, Информация, Предупреждение, Ошибка"` |
| `P/1` с `N/1` | `Levels: "Информация, Предупреждение, Ошибка"` |

Прочие коды уровней, `SplitPeriod` и `EventLogFormat` описаны в справке, но их
запись в ЖР не подтверждена этим набором сценариев. Для них сохраняется общее
дерево. Изменение периода разделения через API в проверенном сценарии не создало
событие с соответствующим содержимым.

<a id="user"></a>
## Пользователь

Поля `values` позиционного JSON для `_$User$_.New`, `Update`, `NewError`,
`UpdateError`. Номер — позиция после маркера `65535`. В форме `65534` отсутствует
`DefaultInterface`, последующие позиции сдвинуты на одну.

| Позиция | Ключ | Тип и содержимое |
|---|---|---|
| 1 | `OSAuthentication` | Логическое |
| 2 | `StandardAuthentication` | Логическое |
| 3 | `CannotChangePassword` | Логическое |
| 4 | `Name` | Строка |
| 5 | `Language` | Ссылка `O` |
| 6 | `PasswordChanged` | Логическое |
| 7 | `PasswordIsSet` | Логическое |
| 8 | `ShowInList` | Логическое |
| 9 | `FullName` | Строка |
| 10 | `OSUser` | Строка |
| 11 | `RunMode` | Исходное строковое значение режима |
| 12 | `Roles` | Массив ссылок `O` |
| 13 | `DefaultInterface` | Ссылка `O`; только `65535` |
| 14 | `OpenIDAuthentication` | Логическое |
| 15 | `UnsafeOperationProtection` | Логическое |
| 16 | `SecondAuthenticationFactorSettings` | Массив строк с именами шаблонов |
| 17 | `SecondAuthenticationFactorSettingsProcessing` | Исходное строковое значение способа обработки |
| 18 | `CannotRecoveryPassword` | Логическое |
| 19 | `Email` | Строка |
| 20 | `OpenIDConnectAuthentication` | Логическое |
| 21 | `AccessTokenAuthentication` | Логическое |
| 22 | `UserMatchingKeys` | Массив строк |
| 23 | `PasswordPolicyName` | Строка |
| 24 | `PasswordSettingDate` | Дата |
| 25 | `PasswordHashAlgorithmType` | Строка |
| 26 | `QRCodeAuthentication` | Логическое |
| 27 | `PasswordDoesNotSatisfyRequirements` | Логическое |
| 28 | `EmailAuthentication` | Логическое |

Ссылка `O` представляется объектом `{"$type":"O","typeId":"...","value":"UUID"}`;
объект не ищется в текущей конфигурации. Пустые массивы сохраняются как `[]`.
Значения режимов, например `Авто` и `НеИспользовать`, сохранены в прежнем виде.
Имена JSON также сохраняют существующий контракт, даже если в XML-выгрузке
платформы часть новых ключей представлена по-русски.

<a id="policy"></a>
## Политика паролей

Поля `values` для `UserPasswordPolicyNew` и `UserPasswordPolicyUpdate`, `P/255`.

| Ключ | Тип |
|---|---|
| `Name` | Строка |
| `PasswordMaxEffectivePeriod` | Число |
| `PasswordMinEffectivePeriod` | Число |
| `PasswordMinLength` | Число |
| `PasswordReuseLimit` | Число |
| `PasswordStrengthCheck` | Логическое |
| `PasswordExpirationNotificationPeriod` | Число |
| `PasswordCompromiseCheck` | Логическое |
| `ActionOnPasswordRequirementsViolationOnAuthenticationCode` | Исходный числовой код действия |

Для удаления политики поддержан отдельный обычный JSON с `Name` (см. каталог).
Числовое действие политики не переименовывается в текст по аналогии с параметрами
базы: это разные проверенные формы.

<a id="parameters"></a>
## Параметры базы

Поля `values` для `ParametersUpdate` и `ParametersUpdateError`. Принимаются только
перечисленные маски и соответствующее им число значений; произвольные комбинации
битов не интерпретируются автоматически.

| Маска | Ключи в порядке записи | Тип или преобразование |
|---|---|---|
| `1` | `LockWaitTime` | Число |
| `2` | `UserPasswordMinLength` | Число |
| `4` | `UserPasswordStrengthCheck` | `N/0` → `"false"`, `N/3` → `"true"`; другие коды не интерпретируются |
| `8` | `PassiveSessionHibernateTime` | Число |
| `16` | `HibernateSessionTerminateTime` | Число |
| `64` | `MaxUnsuccessfulAttemptsCount` | Число |
| `128` | `LockDuration` | Число |
| `192` | `MaxUnsuccessfulAttemptsCount`, `LockDuration` | Число, число |
| `256` | `UserNameAdditionCodes` | Строка с исходными разделителями, не массив |
| `320` | `MaxUnsuccessfulAttemptsCount`, `UserNameAdditionCodes` | Число, строка |
| `384` | `LockDuration`, `UserNameAdditionCodes` | Число, строка |
| `448` | `MaxUnsuccessfulAttemptsCount`, `LockDuration`, `UserNameAdditionCodes` | Число, число, строка |
| `512` | `UserPasswordMinEffectivePeriod` | Число |
| `1024` | `UserPasswordMaxEffectivePeriod` | Число |
| `2048` | `UserPasswordReuseLimit` | Число |
| `4096` | `UserPasswordExpirationNotificationPeriod` | Число |
| `8192` | `UserPasswordHashAlgorithmType` | Строка |
| `16384` | `UserPasswordCompromiseCheck` | Логическое |
| `32768` | `ActionOnUserPasswordRequirementsViolationOnAuthentication` | `N/0` → `Потребовать смену пароля`, `N/1` → `Предложить смену пароля`, `N/2` → `Нет` |
| `65536` | `InactivityTimeForTerminateSessionNotification` | Число |
| `131072` | `InactivityTimeForTerminateSession` | Число |

Изменение количества заданий пересчёта итогов в тестовой базе создало `P/{0}` без
самого значения. Поэтому `TotalsRecalculationJobsCount` из такой записи не
восстанавливается и в список именованных полей не включён.

<a id="regional"></a>
## Региональные настройки

Обычный JSON для `RegionalSettingsChange`.

| Ключ | Тип |
|---|---|
| `LocaleCode` | Строка |
| `UseCurrentSessionSettings` | Логическое |
| `NumbersDecimalSeparator` | Строка |
| `NumbersDigitGroupSeparator` | Строка |
| `NumbersDigitGroupFormat` | Строка |
| `NegativeNumberPresentation` | Число |
| `BooleanFalsePresentation` | Строка |
| `BooleanTruePresentation` | Строка |
| `DatePresentationFormat` | Строка |
| `TimePresentationFormat` | Строка |
| `FirstDayOfWeek` | Число |

<a id="extension"></a>
## Расширение конфигурации

Обычный JSON для четырёх кодов, явно перечисленных в каталоге.

| Ключ | Тип |
|---|---|
| `Name` | Строка |
| `Version` | Строка |
| `Vendor` | Строка |
| `UUID` | Строка |
| `HashSum` | Строка |
| `Synonym` | Строка |
| `SafeMode` | Логическое |
| `SecurityProfile` | Строка |
| `Active` | Логическое |
| `UnsafeOperationProtection` | Логическое |
| `Purpose` | Строковое представление назначения |
| `UsedInDistributedInfoBase` | Логическое |
| `Scope` | Строковое представление области действия |
| `DefaultRoles` | `null` при пустом списке; `{"Item":"Имя"}` при одной роли; `{"Item":["Имя1","Имя2"]}` при нескольких |
| `UseDefaultRolesForAllUsers` | Логическое |

<a id="table-data"></a>
## Табличные данные

Поддержанная форма `Z` содержит уникальные имена колонок с описателем `Pattern`
или `Pattern/{S}` и строковые ячейки. Результат:

| Поле | Представление |
|---|---|
| `Data.Column` | `{"name":"Колонка"}` для одной колонки; массив таких объектов для нескольких |
| `Data.Row` | Объект `{"Колонка":"Значение"}` для одной строки; массив объектов для нескольких; поле отсутствует при нуле строк |

Для другой структуры таблицы сохраняется общее дерево. В частности, наличие
самого типа `Z` не означает поддержку произвольных типов ячеек.

<a id="generic"></a>
## Общий разбор и прикладные события

Эти правила работают также для неизвестных системных кодов и прикладных событий.
Их названия сохраняются дословно, включая пробелы. Дополнительные поля не
придумываются по названию события.

| Содержимое записи | Результат в Data |
|---|---|
| `S` | Строка с восстановлением кавычек; пробелы сохраняются |
| `N` | Исходный десятичный токен строкой, без округления через `double` |
| `D` | Дата строкой без преобразования в UTC |
| `B/0`, `B/1` | Строки `false`, `true` |
| `R` | Исходное строковое значение ссылки |
| `U` | Пустая строка |
| `P` с положительным счётчиком и уникальными парами имя/значение | `1c-named-data-v1`: ключи в `values` взяты из записи; поддержаны `S`, `N`, `B`, `D`, `U`, `@`, `R` |
| Прочая сложная форма, изменённая маска или неподдержанный тип поля | `1c-brackets-v1` с полным деревом `nodes` |

В именованном JSON `U` хранится как `{"$type":"U"}`, ссылки `@`/`R` —
как `{"$type":"@","value":"..."}` / `{"$type":"R","value":"..."}`.
В `nodes` сохраняются исходные тексты листьев, включая удвоенные кавычки и
числовые токены. Это структурное представление разобранной записи, а не побайтовая
копия файла. Синтаксически повреждённые записи обрабатываются отдельно через
диагностику и лог проблемных записей.

## Кандидаты на дальнейший разбор

Состав полей в справке ещё не подтверждает порядок и типы узлов в записи ЖР.
Следующие события остаются общим разбором до получения соответствующего
контрольного журнала и независимой штатной выгрузки.

| События или группа | Что известно из справки | Какого подтверждения не хватает |
|---|---|---|
| `_$Debug$_.DebugTargetAttach`, `DebugTargetDetach`, `ExpressionEvaluation` | `DebugServerUser`, `DebugTargetType`; для вычисления также `Expression` | ЖР сервера отладки с настроенными пользователями; обычное подключение отладчика не доказывает эту форму |
| `_$OpenIDProvider$_.PositiveAssertion`, `NegativeAssertion` | `RelyingPartyURL`; для подтверждения также `Name` | Реальный ответ информационной базы в роли провайдера OpenID |
| `_$Session$_.AuthenticationFirstFactor` | `Name`, способ аутентификации, `CurrentOSUser` | Сценарий входа с двумя факторами и проверка записанного варианта структуры |
| `_$Session$_.PasswordChangeError`, `PasswordCompromiseCheckServiceErrorOnAuthentication` | `Name` | Соответствующий отказ при входе или смене пароля через формы платформы; одноимённая ошибка API не заменяет этот сценарий |
| `_$InfoBase$_.DBConfigExtensionInsert`, `DBConfigExtensionInsertError`, `DBConfigExtensionDeleteError` | Свойства расширения, аналогичные перечисленным выше | Запись конкретной операции при обновлении базы; одинаковый список свойств в справке не гарантирует одинаковый бинарный порядок |
| `_$InfoBase$_.DBConfigBackgroundUpdateError` | `Vendor`, `Version` | Ошибка именно фонового обновления конфигурации базы |
| `_$InfoBase$_.UserPasswordPolicyNewError`, `UserPasswordPolicyUpdateError`, `UserPasswordPolicyDeleteError`; `_$User$_.DeleteError` | Защищённые данные, часть событий описана по аналогии с успешными операциями | Ошибка, создавшая именно системную запись ЖР: дубли имён политик в тесте отклонены до появления таких записей |
| `_$InfoBase$_.RoleUpdate`, `_$User$_.PermissionChange` | Права, ограничения, шаблоны, пользователи и разделители; вложенные структуры и соответствия | Изменение прав при включённом аудите и эталонная выгрузка вложенных значений |
| `_$InfoBase$_.AdministrationParametersChange`, `SecurityProfileChange` | Административные параметры и состав профиля безопасности | Отдельный серверный контур с нужным режимом аудита; проверка КОРП остаётся отдельной |
| `_$InfoBase$_.AdditionalAuthenticationSettingsUpdateError` | В справке есть событие ошибки без полного описания его Data | Реальная системная запись ошибки; не подставляем структуру успешного события автоматически |

У остальных строк с общим разбором в справке либо нет отдельного состава сложных
данных, либо `Data` является значением/ссылкой. Это не доказательство отсутствия
других форм: скаляры уже читаются общими правилами, а нераспознанная структура
сохраняется деревом. Частично поддержанные события также требуют дополнительных
примеров для неперечисленных форм и комбинаций.

## Дополнительный контекст

При `UseEventContext=true` перечисленные результаты помещаются **строкой** в
поле `Data` внешнего JSON-конверта `1c-event-context-v1`:

| Поле конверта | Содержимое |
|---|---|
| `EventCode` | Исходный код события |
| `ApplicationCode` | Исходный код приложения |
| `Data` | Результат разбора, описанный выше, как строка |
| `DataNodes` | Полное дерево исходного содержимого Data |
| `ExtraFields` | Деревья дополнительных полей записи, начиная с индекса 17 |
| `SessionDataSeparation` | Разрешённые значения разделителей либо `null` |
| `SeparationDictionary` | Исходные записи словаря для разрешения разделителей |

По умолчанию флаг выключен. Диагностический `UseStrictParsing` не расширяет
список известных форм: корректная, но неизвестная форма сохраняется деревом.

## Основания и проверка

Имена и назначение полей сверены со справкой установленной платформы:
`shcntx_ru.hbk` → «Глобальный контекст» → `ВыгрузитьЖурналРегистрации` →
`Отбор` → `Событие` (страница `objects/Global context/methods/catalog3403/UnloadEventLog4040.html`). Порядок полей в бинарном ЖР подтверждался отдельно:
контролируемыми изменениями в изолированной базе и сравнением со штатной
выгрузкой. Для защищённых данных пользователей и политик использована выгрузка
`ВыгрузитьЖурналРегистрации` из сеанса администратора; офлайн-выгрузка в проверенном
сценарии возвращала для них `null`.

Совпадение формы событий ошибок проверяется также искусственными вариантами
той же записи. Это не означает, что для каждого кода ошибки был воспроизведён
реальный отказ платформы. Имена и представления существующих JSON-полей
сохранены для совместимости; они не во всех случаях дословно совпадают с ключами
или представлениями значений в XML штатной выгрузки.

| Что проверяется | Реализация и контрольные примеры |
|---|---|
| Названия событий | [CurrentEventNames.cs](../OneSTools.EventLog/CurrentEventNames.cs), [каталог штатных представлений](../OneSTools.EventLog.Tests/Fixtures/event-catalog.json) |
| Обычный JSON и общий именованный разбор | [StructuredEventData.cs](../OneSTools.EventLog/StructuredEventData.cs), [noncorp](../OneSTools.EventLog.Tests/Fixtures/noncorp-data-8.3.27.json), [ERP](../OneSTools.EventLog.Tests/Fixtures/erp-data-8.3.27.json), [простые именованные поля](../OneSTools.EventLog.Tests/Fixtures/known-named-data-8.3.27.json), [общие именованные поля](../OneSTools.EventLog.Tests/Fixtures/named-data-8.3.27.json) |
| Пользователи, политики и параметры | [PositionalEventData.cs](../OneSTools.EventLog/PositionalEventData.cs), [позиционные формы](../OneSTools.EventLog.Tests/Fixtures/positional-data-8.3.27.json) |
| Конверт контекста | [EventContextReader.cs](../OneSTools.EventLog/EventContextReader.cs) |
| Запись JSON и неизменность схемы ClickHouse | [StructuredClickHouseTests.cs](../OneSTools.EventLog.Exporter.IntegrationTests/StructuredClickHouseTests.cs) |

При добавлении нового кода или варианта Data следует обновлять соответствующую
строку каталога и контрольный пример. Изменение одной только справки названий не
даёт основания отмечать новый вариант Data как разобранный по полям.
