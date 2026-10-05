# Code signing policy

## Текущий статус

До одобрения проекта SignPath Foundation предварительные сборки Aden+ могут быть
не подписаны. Это явно указывается на странице соответствующего prerelease.
Финальный релиз не должен выдаваться за подписанный, пока Authenticode-подпись
обоих исполняемых файлов не проверена после сборки.

После одобрения проекта для подписанных релизов действует правило:
**Free code signing provided by [SignPath.io](https://signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).**

## Происхождение подписанных файлов

- подписываются только `Aden+.exe` и `runtime/agent/AdenPlus.Agent.exe`, собранные
  из этого репозитория;
- сборка выполняется GitHub Actions из помеченного тегом коммита;
- каждый запрос production-подписи требует ручного одобрения;
- сторонние файлы среды .NET и библиотек не выдаются за собственные компоненты
  Aden+ и не подписываются сертификатом проекта;
- версия продукта и файлов задаётся одним параметром release-сборки.

## Роли

- Committer и reviewer: [@hellsmenser](https://github.com/hellsmenser).
- Approver запросов подписи: [@hellsmenser](https://github.com/hellsmenser).

Изменения от внешних участников принимаются через pull request и проверяются
сопровождающим. Для GitHub и SignPath используется двухфакторная аутентификация.

## Приватность

Передаваемые и локально хранимые данные описаны в
[политике конфиденциальности](PRIVACY.md). Автономная доставка использует
[Telegram Bot API](https://core.telegram.org/bots/api); применима также
[политика конфиденциальности Telegram](https://telegram.org/privacy).

## Проверка релиза

После скачивания пользователь может проверить подпись и хэш:

```powershell
Get-AuthenticodeSignature .\Aden+.exe | Format-List Status,StatusMessage,SignerCertificate
Get-FileHash .\AdenPlus-v1.0.0-win-x64.zip -Algorithm SHA256
```
