using System.IO;
using System.Text.Json;
using GodjiVpn.Models;
using GodjiVpn.Utils;

namespace GodjiVpn.Services;

/// <summary>
/// Аналог SubscriptionNotifier.kt (Android) — два локальных уведомления о подписке, оба
/// считаются на устройстве по данным, которые приложение и так уже получает при обычном
/// обновлении подписки (см. вызов из App.xaml.cs на каждый SubscriptionRepository.Subscription):
///
/// 1. Скорое окончание — за 3 дня для обычных тарифов, за 12 часов для триала (kind:"trial"),
///    один раз на каждый конкретный expire_at.
/// 2. Успешная оплата — обнаруживается косвенно: оплата происходит вне приложения (сайт или
///    Telegram-бот), отдельного колбэка от бэкенда нет — сравниваем expire_at с прошлым
///    известным при каждом обновлении; если срок сдвинулся вперёд (или тариф перестал быть
///    триальным), значит оплата прошла.
/// </summary>
public sealed class SubscriptionNotifier
{
    private const double RegularThresholdHours = 72.0;
    private const double TrialThresholdHours = 12.0;

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GodjiVpn", "sub-notify.json");

    private sealed class State
    {
        public string? LastExpireAt { get; set; }
        public string? LastKind { get; set; }
        public string? WarnedForExpireAt { get; set; }
    }

    public event Action<string, string>? NotificationRequested;

    public void Check(SubscriptionInfo sub)
    {
        var state = Load();
        var isTrial = sub.Kind == "trial";

        // lastExpireAt == null — первая проверка за процесс/установку, не считаем "оплатой",
        // иначе уведомление приходило бы при первом же входе в аккаунт.
        if (state.LastExpireAt != null &&
            DateTimeOffset.TryParse(sub.ExpireAt, out var current) &&
            DateTimeOffset.TryParse(state.LastExpireAt, out var last))
        {
            var extended = current > last;
            var trialEnded = state.LastKind == "trial" && !isTrial;
            if (extended || trialEnded)
            {
                NotificationRequested?.Invoke("Оплата прошла успешно",
                    $"Подписка «{sub.PlanName}» продлена до {DateFormat.FormatDate(sub.ExpireAt)}.");
                state.WarnedForExpireAt = null; // новый срок — прошлое предупреждение больше не актуально
            }
        }
        state.LastExpireAt = sub.ExpireAt;
        state.LastKind = sub.Kind;
        Save(state);

        if (!DateTimeOffset.TryParse(sub.ExpireAt, out var expireAt)) return;
        var hoursLeft = (expireAt - DateTimeOffset.UtcNow).TotalHours;
        var threshold = isTrial ? TrialThresholdHours : RegularThresholdHours;
        if (hoursLeft >= 0 && hoursLeft <= threshold && state.WarnedForExpireAt != sub.ExpireAt)
        {
            var text = isTrial
                ? "Пробный период заканчивается меньше чем через 12 часов — продлите, чтобы не потерять доступ."
                : $"Подписка «{sub.PlanName}» заканчивается через несколько дней ({DateFormat.FormatDate(sub.ExpireAt)}) — не забудьте продлить.";
            NotificationRequested?.Invoke("Подписка скоро закончится", text);
            state.WarnedForExpireAt = sub.ExpireAt;
            Save(state);
        }
    }

    private static State Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return new State();
            return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State();
        }
        catch { return new State(); }
    }

    private static void Save(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }
        catch { /* лучшее усилие — потеря состояния приведёт максимум к повторному уведомлению */ }
    }
}
