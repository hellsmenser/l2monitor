# Установщик Aden+

Текущий установщик — PowerShell-сценарий для текущего пользователя Windows. Права администратора и MSI не требуются.

- исполняемые файлы устанавливаются в `%LocalAppData%\L2Monitor\app\agent` и `%LocalAppData%\L2Monitor\app\tray`;
- настройки и защищённые через DPAPI секреты сохраняются в `%LocalAppData%\L2Monitor\agent`;
- В автозапуске регистрируется только `Aden+.exe`; он сам запускает внутренний фоновый компонент
- обновление заменяет файлы приложения, сохраняя пользовательские настройки;
- удаление стирает исполняемые файлы и запись автозапуска; параметр `-RemoveUserData` также удаляет пользовательские данные агента.

Основные сценарии:

- `installer\Install-L2Monitor.ps1`
- `installer\Uninstall-L2Monitor.ps1`
- `installer\Verify-L2MonitorInstaller.ps1`
