using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodjiVpn.Models;
using GodjiVpn.Services;
using GodjiVpn.Utils;

namespace GodjiVpn.ViewModels;

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

    private readonly ApiClient _api;
    private readonly SubscriptionRepository _subscription;

    private List<PlanInfo> _rawPlans = new();

    [ObservableProperty] private string planName = "—";
    [ObservableProperty] private string expiryLabel = "—";
    [ObservableProperty] private int daysLeft;
    [ObservableProperty] private int deviceLimit;
    [ObservableProperty] private string? customerId;
    [ObservableProperty] private bool refreshing;
    [ObservableProperty] private int selectedMonths = 1;

    public ObservableCollection<PeriodItem> Periods { get; } = new();
    public ObservableCollection<PlanItem> Plans { get; } = new();

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
    }

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
    private void CopyCustomerId()
    {
        if (!string.IsNullOrEmpty(CustomerId)) Clipboard.SetText(CustomerId);
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
