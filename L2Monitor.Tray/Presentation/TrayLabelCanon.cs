using L2Monitor.Core.Api;
using L2Monitor.Tray.Api;

namespace L2Monitor.Tray.Presentation;

internal static class TrayLabelCanon
{
    public static string DescribeFailure(Exception exception) =>
        exception switch
        {
            AgentConnectionException => "Не удалось связаться с локальным агентом.",
            LocalControlApiException { ErrorCode: "validation_failed" } => "Проверьте введённые настройки.",
            LocalControlApiException => "Агент не смог выполнить запрос. Повторите попытку.",
            _ => "Не удалось выполнить действие.",
        };

    public static string DescribeActionResult(AgentActionResultDto result) =>
        (result.Action, result.Outcome) switch
        {
            ("test-notification", "completed" or "delivered") => "Проверочное сообщение доставлено в Telegram.",
            ("test-notification", "rejected") => "Настройте токен и чат Telegram перед проверкой.",
            ("test-notification", "failed") => "Не удалось отправить проверочное сообщение в Telegram.",
            ("reload", "completed") => "Конфигурация перечитана.",
            ("restart-agent", "scheduled" or "accepted" or "completed") => "Перезапуск агента запланирован.",
            ("restart-agent", "rejected") => "Не удалось запланировать перезапуск агента.",
            ("confirm-telegram-chat-link", "completed") => "Чат Telegram привязан.",
            ("confirm-telegram-chat-link", _) => "Не удалось подтвердить привязку чата Telegram.",
            (_, "completed" or "delivered") => "Действие выполнено.",
            _ => "Действие не выполнено. Проверьте состояние агента.",
        };

    public static string DescribeConnectionState(string? state) =>
        state switch
        {
            "Connected" => "Игра ок",
            "SuspectedGhostDisconnect" => "Дисконнект",
            "Connecting" => "Подключается",
            "Disconnected" => "Дисконнект",
            _ => string.IsNullOrWhiteSpace(state) ? string.Empty : "Неизвестно",
        };

    public static string DescribeIncidentKind(string? kind) =>
        kind switch
        {
            "ghost_disconnect_suspected" => "Дисконнект",
            "client_disconnected" => "Дисконнект",
            "dead_started" => "Смерть",
            "process_exited" => "Клиент закрыт",
            "process_exited_while_dead" => "Клиент закрыт",
            "timeout_exceeded" => "Дисконнект",
            "delivery_degraded" => "Проблема с доставкой",
            "backend_degraded" => "Проблема с облаком",
            "agent_started" => "Агент запущен",
            "agent_stopped" => "Агент остановлен",
            "agent_faulted" => "Сбой агента",
            "agent_recovered_after_unclean_shutdown" => "Восстановление после сбоя",
            "agent_recovered_after_fault" => "Восстановление после сбоя",
            "agent_recovered_with_unreadable_runtime_state" => "Восстановление после сбоя",
            "settings_updated" => "Настройки сохранены",
            "tray_settings_updated" => "Настройки сохранены",
            "telegram_secret_updated" => "Токен Telegram обновлён",
            "cloud_secret_updated" => "Ключ облака обновлён",
            "telegram_chat_linked" => "Чат Telegram привязан",
            "configuration_reloaded" => "Конфиг перечитан",
            "restart_requested" => "Запрошен перезапуск",
            _ => string.IsNullOrWhiteSpace(kind) ? string.Empty : "Другое событие",
        };

    public static string DescribeSeverity(string? severity) =>
        severity switch
        {
            "info" => "Инфо",
            "warning" => "Предупреждение",
            "error" => "Ошибка",
            _ => string.IsNullOrWhiteSpace(severity) ? string.Empty : "Неизвестно",
        };

    public static string DescribeAgentStatus(string? status) =>
        status switch
        {
            "Starting" => "Запускается",
            "Running" => "Работает",
            "Stopped" => "Остановлен",
            "Faulted" => "Ошибка",
            _ => string.IsNullOrWhiteSpace(status) ? string.Empty : "Неизвестно",
        };

    public static string DescribeHealthState(string? state) =>
        state switch
        {
            "healthy" => "Исправно",
            "configured" => "Настроено",
            "disabled" => "Выключено",
            "unknown" => "Неизвестно",
            "not_configured" => "Не настроено",
            "misconfigured" => "Нужна настройка",
            "auth_failed" => "Ошибка авторизации",
            "unreachable" => "Недоступно",
            "protocol_error" => "Ошибка сервиса",
            "rate_limited" => "Лимит запросов",
            "skipped" => "Не проверялось",
            _ => string.IsNullOrWhiteSpace(state) ? string.Empty : "Неизвестно",
        };

    public static string DescribeCachedCloudHealth(ComponentHealthDto? health) =>
        health?.State?.ToLowerInvariant() switch
        {
            "healthy" => "Облако доступно.",
            "connecting" => "Подключение к облаку устанавливается.",
            "unreachable" => "Облако недоступно.",
            "auth_failed" => "Облако отклонило ключ авторизации.",
            "protocol_error" => "Ошибка при обмене данными с облаком.",
            "disabled" => "Проверка облака выключена.",
            _ => "Состояние облака неизвестно.",
        };

    public static string DescribeHealthSummary(ComponentHealthDto? health)
    {
        if (health is null)
        {
            return string.Empty;
        }

        return DescribeHealthSummary(health.State, health.Summary);
    }

    public static string DescribeHealthSummary(string? state, string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return DescribeHealthState(state);
        }

        return summary switch
        {
            "Delivery mode is disabled." => "Доставка выключена.",
            "Telegram delivery requires DeliveryEnabled and a target chat id." => "Для доставки в Telegram включите доставку и привяжите чат.",
            "Telegram delivery requires a stored bot token." => "Для доставки в Telegram сохраните токен бота.",
            "Telegram local delivery is configured." => "Локальная доставка через Telegram настроена.",
            "Cloud delivery requires a stored auth key." => "Для облачной доставки сохраните ключ авторизации.",
            "Cloud delivery is configured." => "Облачная доставка настроена.",
            "Cloud delivery requires an absolute BackendBaseUrl." => "В этой сборке не задан адрес сервиса Aden+.",
            "Cloud delivery requires BackendBaseUrl to be an absolute http(s) URL." => "В этой сборке неверно настроен адрес сервиса Aden+.",
            "Backend health applies only in Cloud mode." => "Проверка облака доступна только в режиме «Облако».",
            "Backend checks apply only when delivery mode is Cloud." => "Проверка облака доступна только в режиме «Облако».",
            "Backend connectivity has not been checked yet." => "Подключение к облаку ещё не проверялось.",
            "Backend ping succeeded." => "Облако отвечает.",
            "Cloud relay accepted notification." => "Облачная доставка работает.",
            _ => DescribeDynamicSummary(summary) ?? DescribeHealthState(state),
        };
    }

    public static string DescribeIncidentSummary(AgentIncidentDto incident) =>
        DescribeIncidentSummary(incident.Kind, incident.Summary);

    public static string DescribeIncidentSummary(string? kind, string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return DescribeIncidentKind(kind);
        }

        return kind switch
        {
            "settings_updated" => "Настройки агента сохранены.",
            "tray_settings_updated" => "Изменения настроек сохранены.",
            "telegram_secret_updated" => "Токен Telegram обновлён.",
            "cloud_secret_updated" => "Ключ облака обновлён.",
            "telegram_chat_linked" => "Чат Telegram привязан.",
            "configuration_reloaded" => "Конфигурация агента перечитана с диска.",
            "restart_requested" => "Запрошен перезапуск агента.",
            "agent_started" => "Агент запущен.",
            "agent_stopped" => "Агент остановлен.",
            "delivery_degraded" => DescribeHealthSummary(null, summary),
            "backend_degraded" => DescribeHealthSummary(null, summary),
            _ => DescribeDynamicSummary(summary) ?? DescribeIncidentKind(kind),
        };
    }

    private static string? DescribeDynamicSummary(string summary)
    {
        if (summary.StartsWith("L2Monitor: possible ghost disconnect for pid ", StringComparison.Ordinal)
            && summary.EndsWith(". Gameplay socket is still present, but the session socket disappeared.", StringComparison.Ordinal))
        {
            return TranslatePidSummary(
                summary,
                "L2Monitor: possible ghost disconnect for pid ",
                ". Gameplay socket is still present, but the session socket disappeared.",
                "Похоже на дисконнект у ",
                ": игровой сокет ещё активен, но сокет сессии уже пропал.");
        }

        if (summary.StartsWith("L2Monitor: client pid ", StringComparison.Ordinal)
            && summary.EndsWith(" lost active game connections.", StringComparison.Ordinal))
        {
            return TranslatePidSummary(
                summary,
                "L2Monitor: client pid ",
                " lost active game connections.",
                string.Empty,
                " потерял активные игровые соединения.");
        }

        if (summary.StartsWith("L2Monitor: client pid ", StringComparison.Ordinal)
            && summary.EndsWith(" closed.", StringComparison.Ordinal))
        {
            return TranslatePidSummary(
                summary,
                "L2Monitor: client pid ",
                " closed.",
                string.Empty,
                " закрыт.");
        }

        if (summary.StartsWith("L2Monitor: client pid ", StringComparison.Ordinal)
            && summary.Contains(") exited after being disconnected for ", StringComparison.Ordinal)
            && summary.EndsWith("s.", StringComparison.Ordinal))
        {
            return TranslatePidSummaryWithDuration(
                summary,
                "L2Monitor: client pid ",
                ") exited after being disconnected for ",
                " был закрыт после дисконнекта через ",
                " с.");
        }

        if (summary.StartsWith("L2Monitor: client pid ", StringComparison.Ordinal)
            && summary.Contains(") timed out after ", StringComparison.Ordinal)
            && summary.EndsWith("s.", StringComparison.Ordinal))
        {
            return TranslatePidSummaryWithDuration(
                summary,
                "L2Monitor: client pid ",
                ") timed out after ",
                " не отвечал ",
                " с.");
        }

        if (summary.StartsWith("L2Monitor: client pid ", StringComparison.Ordinal)
            && summary.Contains(") recovered after ", StringComparison.Ordinal)
            && summary.EndsWith("s.", StringComparison.Ordinal))
        {
            return TranslatePidSummaryWithDuration(
                summary,
                "L2Monitor: client pid ",
                ") recovered after ",
                " восстановился через ",
                " с.");
        }

        if (summary.StartsWith("Client ", StringComparison.Ordinal)
            && summary.EndsWith(" lost active game connections.", StringComparison.Ordinal))
        {
            return ReplaceClientSummaryEnding(
                summary,
                " lost active game connections.",
                " потерял активные игровые соединения.");
        }

        if (summary.StartsWith("Client ", StringComparison.Ordinal)
            && summary.EndsWith(" exited.", StringComparison.Ordinal))
        {
            return ReplaceClientSummaryEnding(summary, " exited.", " закрыт.");
        }

        if (summary.StartsWith("Possible ghost disconnect for ", StringComparison.Ordinal)
            && summary.EndsWith(". Session socket disappeared while gameplay socket is still present.", StringComparison.Ordinal))
        {
            return "Похоже на дисконнект: игровой сокет ещё есть, но сокет сессии уже пропал.";
        }

        if (summary.StartsWith("Detected character death audio for ", StringComparison.Ordinal)
            && summary.EndsWith(".", StringComparison.Ordinal))
        {
            return ReplaceSimpleSuffix(summary, "Detected character death audio for ", "Зафиксирован звук смерти персонажа у ");
        }

        if (string.Equals(summary, "Detected character death audio in the captured game stream.", StringComparison.Ordinal))
        {
            return "Зафиксирован звук смерти в игровом аудиопотоке.";
        }

        if (summary.StartsWith("Telegram delivered to chat_id=", StringComparison.Ordinal))
        {
            return "Telegram: сообщение доставлено.";
        }

        if (string.Equals(summary, "Telegram unreachable.", StringComparison.Ordinal)
            || summary.StartsWith("Telegram unreachable: ", StringComparison.Ordinal))
        {
            return "Telegram недоступен.";
        }

        if (string.Equals(summary, "Telegram timed out.", StringComparison.Ordinal)
            || summary.StartsWith("Telegram timed out: ", StringComparison.Ordinal))
        {
            return "Telegram не ответил вовремя.";
        }

        if (summary.StartsWith("Telegram rejected the request: ", StringComparison.Ordinal))
        {
            return "Telegram отклонил запрос.";
        }

        if (string.Equals(summary, "Telegram rejected the request.", StringComparison.Ordinal))
        {
            return "Telegram отклонил запрос.";
        }

        if (summary.StartsWith("Telegram auth failed (", StringComparison.Ordinal))
        {
            return summary.Replace("Telegram auth failed", "Telegram: ошибка авторизации", StringComparison.Ordinal);
        }

        if (string.Equals(summary, "Telegram rate limited the request.", StringComparison.Ordinal))
        {
            return "Telegram: достигнут лимит запросов.";
        }

        if (summary.StartsWith("Telegram returned ", StringComparison.Ordinal))
        {
            return summary.Replace("Telegram returned ", "Telegram вернул ошибку ", StringComparison.Ordinal);
        }

        if (summary.StartsWith("Cloud relay unreachable: ", StringComparison.Ordinal))
        {
            return "Облачный сервис недоступен.";
        }

        if (summary.StartsWith("Cloud relay timed out: ", StringComparison.Ordinal))
        {
            return "Облачный сервис не ответил вовремя.";
        }

        if (summary.StartsWith("Cloud relay auth failed (", StringComparison.Ordinal))
        {
            return summary.Replace("Cloud relay auth failed", "Облачный сервис отклонил ключ авторизации", StringComparison.Ordinal);
        }

        if (string.Equals(summary, "Cloud relay rate limited the request.", StringComparison.Ordinal))
        {
            return "Облачный сервис ограничил запросы.";
        }

        if (summary.StartsWith("Cloud relay returned ", StringComparison.Ordinal))
        {
            return summary.Replace("Cloud relay returned ", "Облачный сервис вернул ошибку ", StringComparison.Ordinal);
        }

        if (summary.StartsWith("Backend ping failed: ", StringComparison.Ordinal))
        {
            return "Облако недоступно.";
        }

        if (summary.StartsWith("Backend rejected the auth key (", StringComparison.Ordinal))
        {
            return summary.Replace("Backend rejected the auth key", "Облако отклонило ключ авторизации", StringComparison.Ordinal);
        }

        if (summary.StartsWith("Backend returned ", StringComparison.Ordinal))
        {
            return summary.Replace("Backend returned ", "Облако вернуло ошибку ", StringComparison.Ordinal);
        }

        if (summary.StartsWith("Previous agent run ended without a clean stop", StringComparison.Ordinal))
        {
            return "Прошлый запуск агента завершился без корректной остановки.";
        }

        if (summary.StartsWith("Previous agent run faulted before restart", StringComparison.Ordinal))
        {
            return "Прошлый запуск агента завершился сбоем.";
        }

        if (summary.StartsWith("Previous agent runtime-state.json could not be read", StringComparison.Ordinal))
        {
            return "Не удалось прочитать состояние прошлого запуска агента.";
        }

        return null;
    }

    private static string? ReplaceClientSummaryEnding(
        string summary,
        string englishEnding,
        string replacementEnding)
    {
        var idStart = summary.LastIndexOf(" (", StringComparison.Ordinal);
        var idEnd = summary.Length - englishEnding.Length - 1;
        if (idStart <= "Client ".Length
            || idEnd <= idStart + 2
            || summary[idEnd] != ')')
        {
            return null;
        }

        return $"{summary["Client ".Length..idStart]}{replacementEnding}";
    }

    private static string ReplaceSimpleSuffix(string summary, string englishPrefix, string russianPrefix) =>
        russianPrefix + summary[englishPrefix.Length..];

    private static string? TranslatePidSummary(
        string summary,
        string englishPrefix,
        string englishSuffix,
        string russianPrefix,
        string russianSuffix)
    {
        var nameStart = summary.IndexOf('(', englishPrefix.Length);
        var nameEnd = summary.LastIndexOf(')');
        var pidText = nameStart < 0
            ? string.Empty
            : summary[englishPrefix.Length..nameStart].Trim();
        if (nameStart < 0
            || nameEnd <= nameStart + 1
            || !string.Equals(summary[(nameEnd + 1)..], englishSuffix, StringComparison.Ordinal)
            || !int.TryParse(pidText, out _))
        {
            return null;
        }

        var name = summary[(nameStart + 1)..nameEnd];
        return $"{russianPrefix}{name}{russianSuffix}";
    }

    private static string? TranslatePidSummaryWithDuration(
        string summary,
        string englishPrefix,
        string separator,
        string russianMiddle,
        string russianSuffix)
    {
        var separatorIndex = summary.IndexOf(separator, englishPrefix.Length, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return null;
        }

        var nameStart = summary.IndexOf('(', englishPrefix.Length);
        var pidText = nameStart < 0
            ? string.Empty
            : summary[englishPrefix.Length..nameStart].Trim();
        var durationText = summary[(separatorIndex + separator.Length)..^2];
        if (nameStart < 0
            || nameStart + 1 >= separatorIndex
            || !int.TryParse(pidText, out _)
            || !int.TryParse(durationText, out _))
        {
            return null;
        }

        var name = summary[(nameStart + 1)..separatorIndex];
        return $"{name}{russianMiddle}{durationText}{russianSuffix}";
    }
}
