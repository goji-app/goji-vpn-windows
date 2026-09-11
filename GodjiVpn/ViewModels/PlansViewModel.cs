using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodjiVpn.Models;
using GodjiVpn.Services;
using GodjiVpn.Utils;

namespace GodjiVpn.ViewModels;

/// <summary>Одна новость/рассылка (см. SubscriptionRepository.Broadcasts). Document
/// перестраивается один раз при клике "Читать полностью" (см. RichContent.Build —
/// collapsedBlocks сворачивает контент до 6 верхнеуровневых блоков, как на самом сайте).</summary>
public sealed partial class NewsItem : ObservableObject
{
    private const int CollapsedBlockCount = 6;
    private bool _expanded;

    public required string Id { get; init; }
    public required string RawContent { get; init; }
    public required string DateLabel { get; init; }
    public required List<BroadcastButtonDto> Buttons { get; init; }

    [ObservableProperty] private FlowDocument document = new();

    public void BuildCollapsed() => Document = RichContent.Build(RawContent, CollapsedBlockCount, Expand);

    private void Expand()
    {
        if (_expanded) return;
        _expanded = true;
        Document = RichContent.Build(RawContent);
    }
}

/// <summary>Один приглашённый пользователь — имя/юзернейм/email уже замаскированы наполовину
/// точками (см. PlansViewModel.MaskHalf/DisplayNameFor), как и на самом сайте: это персональные
/// данные третьих лиц, не наши, полностью открытым текстом их показывать не стоит.</summary>
public sealed class ReferralEntryItem
{
    public required string DisplayName { get; init; }
    public required bool IsActive { get; init; }
    public required int BonusDays { get; init; }
    public string StatusBadge => IsActive ? "Активен" : "Неактивен";
    public string BonusLabel => BonusDays > 0 ? $"+{BonusDays} дн." : "";
    public bool HasBonusLabel => BonusDays > 0;
}

public sealed class ReferralUiModel
{
    public required string Link { get; init; }
    public required int TotalReferrals { get; init; }
    public required int ActiveReferrals { get; init; }
    public required int TotalBonusDays { get; init; }
    public required List<ReferralEntryItem> Entries { get; init; }
    public bool HasEntries => Entries.Count > 0;
}

/// <summary>Только статус/сводка партнёрской программы — подача заявки и запрос вывода средств
/// делаются на сайте (та же логика, что и "Продлить" для тарифов: не переизобретаем денежные
/// формы нативно). Computed-флаги ниже — чтобы XAML мог выбирать нужный блок простым
/// Visibility-биндингом, без аналога Kotlin `when` в разметке.</summary>
public sealed class PartnerUiModel
{
    public required bool IsPartner { get; init; }
    public required bool IsActive { get; init; }
    public required string? ApplicationStatus { get; init; }
    public required double CommissionRate { get; init; }
    public required int ClientCount { get; init; }
    public required double TotalEarned { get; init; }
    public required double AvailableBalance { get; init; }
    public required double PendingBalance { get; init; }

    public bool IsDeactivated => IsPartner && !IsActive;
    public bool IsActivePartner => IsPartner && IsActive;
    public bool IsPending => !IsPartner && ApplicationStatus == "pending";
    public bool IsRejected => !IsPartner && ApplicationStatus == "rejected";
    public bool IsNotApplied => !IsPartner && ApplicationStatus == null;
    public bool ShowApplyButton => IsRejected || IsNotApplied;
    public bool ShowActionButton => IsActivePartner || ShowApplyButton;
    public string ActionButtonLabel => IsActivePartner ? "Открыть кабинет" : "Подать заявку";
    public bool ShowPendingBalance => PendingBalance > 0;
    public string CommissionLabel => $"{CommissionRate}%";
    public string EarnedLabel => $"{(int)TotalEarned} ₽";
    public string AvailableBalanceLabel => $"{(int)AvailableBalance} ₽";
    public string PendingBalanceLabel => $"{(int)PendingBalance} ₽";
}

public sealed partial class PeriodItem : ObservableObject
{
    public required int Months { get; init; }
    public required string Label { get; init; }
    [ObservableProperty] private bool isSelected;
}

public sealed partial class PlanItem : ObservableObject
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string PriceLabel { get; init; }
    public required bool IsCurrent { get; init; }
}

/// <summary>Аналог PlansScreen.kt/PlansViewModel.kt — своя подписка, DaysRing, тарифы
/// (информационно, покупка — только по внешней ссылке на сайт, в API её нет).</summary>
public sealed partial class PlansViewModel : ObservableObject
{
    private const string RenewUrl = "https://gojihub.xyz/#/plans";
    private const string SupportUrl = "https://gojihub.xyz/#/support-chat";

    // Свёрнутый вид (по умолчанию) — только 2 последние новости, без пагинации. Развёрнутый
    // (по клику "Показать все") — постранично, максимум 3 на странице.
    private const int CollapsedNewsCount = 2;
    private const int NewsPageSize = 3;

    private readonly ApiClient _api;
    private readonly SubscriptionRepository _subscription;

    private List<PlanInfo> _rawPlans = new();
    private List<NewsItem> _allNews = new();

    [ObservableProperty] private string planName = "—";
    [ObservableProperty] private string expiryLabel = "—";
    [ObservableProperty] private int daysLeft;
    [ObservableProperty] private int deviceLimit;
    [ObservableProperty] private string? customerId;
    [ObservableProperty] private bool refreshing;
    [ObservableProperty] private int selectedMonths = 1;
    [ObservableProperty] private bool isNewsExpanded;
    [ObservableProperty] private int newsPageIndex;
    [ObservableProperty] private ReferralUiModel? referral;
    [ObservableProperty] private PartnerUiModel? partner;

    public ObservableCollection<PeriodItem> Periods { get; } = new();
    public ObservableCollection<PlanItem> Plans { get; } = new();

    /// <summary>То, что реально показывает XAML — свёрнутый список (2 последние) или текущая
    /// страница (3 на страницу), в зависимости от IsNewsExpanded. См. RefreshVisibleNews.</summary>
    public ObservableCollection<NewsItem> VisibleNews { get; } = new();

    public int NewsTotalPages => _allNews.Count == 0 ? 1 : (int)Math.Ceiling(_allNews.Count / (double)NewsPageSize);
    public bool NewsHasMultiplePages => IsNewsExpanded && NewsTotalPages > 1;
    public bool HasHiddenNews => _allNews.Count > CollapsedNewsCount;
    public string NewsToggleLabel => IsNewsExpanded ? "Свернуть" : "Показать все новости";
    public string NewsPageLabel => $"Страница {NewsPageIndex + 1} из {NewsTotalPages}";

    public PlansViewModel(ApiClient api, SubscriptionRepository subscription)
    {
        _api = api;
        _subscription = subscription;
    }

    public async Task LoadAsync()
    {
        await _subscription.RefreshAsync();
        var sub = _subscription.Subscription;
        PlanName = sub?.PlanName ?? "—";
        ExpiryLabel = sub?.ExpireAt is { } iso ? DateFormat.FormatDate(iso) : "—";
        DaysLeft = sub?.DaysLeft ?? 0;
        DeviceLimit = sub?.DeviceLimit ?? 0;
        CustomerId = _subscription.SelectedNode?.Uuid;

        // Свежая по CreatedAt первая — порядок с бэкенда не гарантирован (см. BroadcastNotifier).
        _allNews = _subscription.Broadcasts.OrderByDescending(b => b.CreatedAt).Select(b =>
        {
            var item = new NewsItem
            {
                Id = b.Id,
                RawContent = b.Content,
                DateLabel = DateFormat.FormatDate(b.CreatedAt),
                Buttons = b.Buttons ?? new List<BroadcastButtonDto>()
            };
            item.BuildCollapsed();
            return item;
        }).ToList();
        IsNewsExpanded = false;
        NewsPageIndex = 0;
        RefreshVisibleNews();

        try
        {
            var response = await _api.GetPlansAsync();
            _rawPlans = response.Plans;

            var months = _rawPlans.SelectMany(p => p.Prices)
                .Where(p => p.PriceType == "base")
                .Select(p => p.PeriodValue)
                .Distinct().OrderBy(m => m).ToList();

            Periods.Clear();
            foreach (var m in months)
                Periods.Add(new PeriodItem { Months = m, Label = m == 1 ? "1 месяц" : $"{m} мес." });

            SelectedMonths = months.FirstOrDefault(1);
            RebuildPlanCards();
        }
        catch
        {
            // Тарифы — вспомогательная информация; неудачная загрузка не должна мешать
            // остальному экрану (подписка/срок уже подтянуты выше).
        }

        // referral_enabled/partner_program_enabled не проверяются отдельным запросом настроек:
        // если фичу когда-нибудь выключат на бэкенде, эндпоинт просто перестанет отвечать
        // успешно, и секция тихо не покажется (тот же принцип, что уже у broadcasts/plans).
        try
        {
            var r = await _api.GetReferralsAsync();
            Referral = new ReferralUiModel
            {
                Link = r.Link,
                TotalReferrals = r.Summary.TotalReferrals,
                ActiveReferrals = r.Summary.ActiveReferrals,
                TotalBonusDays = r.Summary.TotalBonusDays,
                Entries = r.Referrals.Select(e => new ReferralEntryItem
                {
                    DisplayName = DisplayNameFor(e),
                    IsActive = e.IsActive,
                    BonusDays = e.BonusDays
                }).ToList()
            };
        }
        catch { Referral = null; }

        try
        {
            var p = await _api.GetPartnerStatusAsync();
            Partner = new PartnerUiModel
            {
                IsPartner = p.IsPartner,
                IsActive = p.Partner?.IsActive ?? false,
                ApplicationStatus = p.Application?.Status,
                CommissionRate = p.Partner?.CommissionRate ?? 0,
                ClientCount = p.Stats?.ClientCount ?? 0,
                TotalEarned = p.Partner?.TotalEarned ?? 0,
                AvailableBalance = p.Partner?.AvailableBalance ?? 0,
                PendingBalance = p.Partner?.PendingBalance ?? 0
            };
        }
        catch { Partner = null; }
    }

    /// <summary>Веб-версия маскирует половину имени/юзернейма/локальной части email точками —
    /// повторяем то же самое, а не показываем приглашённых пользователей полностью открытым
    /// текстом (это не наши данные, а личные данные третьих лиц).</summary>
    private static string MaskHalf(string s)
    {
        if (s.Length == 0) return s;
        var visible = (s.Length + 1) / 2;
        return s[..visible] + new string('•', s.Length - visible);
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        return at < 0 ? MaskHalf(email) : MaskHalf(email[..at]) + email[at..];
    }

    private static string DisplayNameFor(ReferralEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.TgUsername)) return "@" + MaskHalf(e.TgUsername);
        var fullName = string.Join(" ", new[] { e.TgFirstName, e.TgLastName }.Where(s => !string.IsNullOrEmpty(s))).Trim();
        if (fullName.Length > 0) return MaskHalf(fullName);
        if (!string.IsNullOrWhiteSpace(e.Email)) return MaskEmail(e.Email);
        return $"ID: {e.RefereeTelegramId ?? e.RefereeId ?? 0}";
    }

    [RelayCommand]
    private void CopyReferralLink()
    {
        if (Referral is { } r) Clipboard.SetText(r.Link);
    }

    [RelayCommand]
    private void OpenPartnerDashboard() => OpenUrl("https://gojihub.xyz/#/partner-dashboard");

    [RelayCommand]
    private void SelectPeriod(int months)
    {
        SelectedMonths = months;
        foreach (var p in Periods) p.IsSelected = p.Months == months;
        RebuildPlanCards();
    }

    private void RebuildPlanCards()
    {
        Plans.Clear();
        foreach (var plan in _rawPlans)
        {
            var price = plan.Prices.FirstOrDefault(p => p.PriceType == "base" && p.PeriodValue == SelectedMonths)
                ?? plan.Prices.FirstOrDefault(p => p.PriceType == "base");
            var priceLabel = price != null ? $"{price.Price} {price.Currency}" : "—";
            Plans.Add(new PlanItem
            {
                Id = plan.Id,
                Name = plan.Name,
                Description = plan.Description,
                PriceLabel = priceLabel,
                IsCurrent = plan.Name == PlanName
            });
        }
        foreach (var p in Periods) p.IsSelected = p.Months == SelectedMonths;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Refreshing = true;
        await LoadAsync();
        Refreshing = false;
    }

    [RelayCommand]
    private void OpenRenew() => OpenUrl(RenewUrl);

    [RelayCommand]
    private void OpenSupport() => OpenUrl(SupportUrl);

    [RelayCommand]
    private void OpenBroadcastButton(string url) => OpenUrl(url);

    [RelayCommand]
    private void ToggleNewsExpanded()
    {
        IsNewsExpanded = !IsNewsExpanded;
        NewsPageIndex = 0;
        RefreshVisibleNews();
    }

    [RelayCommand(CanExecute = nameof(CanGoNewsPrevPage))]
    private void NewsPrevPage() { NewsPageIndex--; RefreshVisibleNews(); }
    private bool CanGoNewsPrevPage() => NewsPageIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoNewsNextPage))]
    private void NewsNextPage() { NewsPageIndex++; RefreshVisibleNews(); }
    private bool CanGoNewsNextPage() => NewsPageIndex < NewsTotalPages - 1;

    /// <summary>Свёрнутый вид — 2 последние новости целиком, без пагинации (см.
    /// CollapsedNewsCount). Развёрнутый — постранично по NewsPageSize (см. IsNewsExpanded).</summary>
    private void RefreshVisibleNews()
    {
        VisibleNews.Clear();
        var page = IsNewsExpanded
            ? _allNews.Skip(NewsPageIndex * NewsPageSize).Take(NewsPageSize)
            : _allNews.Take(CollapsedNewsCount);
        foreach (var item in page) VisibleNews.Add(item);

        OnPropertyChanged(nameof(NewsTotalPages));
        OnPropertyChanged(nameof(NewsHasMultiplePages));
        OnPropertyChanged(nameof(HasHiddenNews));
        OnPropertyChanged(nameof(NewsToggleLabel));
        OnPropertyChanged(nameof(NewsPageLabel));
        NewsPrevPageCommand.NotifyCanExecuteChanged();
        NewsNextPageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void CopyCustomerId()
    {
        if (!string.IsNullOrEmpty(CustomerId)) Clipboard.SetText(CustomerId);
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
