using System.IO;

namespace GodjiVpn.Services;

public readonly record struct TrafficDaySnapshot(int DayNumber, long UsedBytes);

/// <summary>[HasData] отличает "расход честно посчитан и равен нулю" от "снимков за этот день
/// ещё/уже не существует" — первый день, за который вообще есть хоть один снимок, не может дать
/// дельту (не с чем сравнивать), и это НЕ то же самое, что подтверждённый нулевой расход.</summary>
public readonly record struct TrafficDayUsage(DateOnly Date, long Bytes, bool HasData);

/// <summary>
/// Бэкенд отдаёт только ТЕКУЩИЙ суммарный расход трафика (SubscriptionInfo.Traffic.UsedBytes) за
/// весь платёжный период — истории по дням он не хранит вообще. Строим её сами: при каждом
/// успешном SubscriptionRepository.RefreshAsync() запоминаем сегодняшний used_bytes
/// (перезаписывая запись за сегодня, если уже есть), а дневной расход считаем задним числом как
/// разницу между соседними днями. История копится только с момента установки этого обновления —
/// глубже заглянуть невозможно, бэкенд этих данных просто не хранит. Порт из Android
/// TrafficHistoryRepository.kt (включая последующий фикс gap-tolerant дельт).
/// </summary>
public sealed class TrafficHistoryRepository
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GodjiVpn", "traffic-history.txt");

    // Простой ручной формат "day:bytes;day:bytes" вместо JSON — записей всего пара десятков и
    // формат предельно простой, заводить сюда сериализатор незачем.
    private static string Encode(List<TrafficDaySnapshot> list) =>
        string.Join(";", list.Select(s => $"{s.DayNumber}:{s.UsedBytes}"));

    private static List<TrafficDaySnapshot> Decode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<TrafficDaySnapshot>();
        var result = new List<TrafficDaySnapshot>();
        foreach (var entry in raw.Split(';'))
        {
            var parts = entry.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var day) && long.TryParse(parts[1], out var bytes))
                result.Add(new TrafficDaySnapshot(day, bytes));
        }
        return result;
    }

    /// <summary>usedBytes — суммарный расход за весь текущий период (то же поле, что уже
    /// показывается на экране "Подписка"), не дневная дельта.</summary>
    public void RecordToday(long usedBytes)
    {
        var today = DateOnly.FromDateTime(DateTime.Now).DayNumber;
        try
        {
            var updated = Load().Where(s => s.DayNumber != today).Append(new TrafficDaySnapshot(today, usedBytes))
                .OrderBy(s => s.DayNumber).ToList();
            // Не даём списку расти бесконечно — 60 дней с запасом хватает для любого разумного графика.
            if (updated.Count > 60) updated = updated[^60..];
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, Encode(updated));
        }
        catch { /* лучшее усилие — график просто не пополнится этим днём */ }
    }

    /// <summary>Дневной расход (не суммарный) за последние [days] дней, включая сегодня. Берём
    /// дельту между ЛЮБЫМИ двумя соседними по времени снимками (не обязательно сутки друг за
    /// другом) и равномерно размазываем её по всем дням разрыва — так пропуск в днях (приложение
    /// не открывали, например) даёт честный средний расход за период вместо однодневного ложного
    /// всплеска или тишины. Отрицательная разница (начался новый платёжный период, used_bytes
    /// обнулился) считается за 0, а не "минус трафик". Дни ДО самого первого снимка (истории ещё
    /// физически не существует) помечены HasData=false, а не молча приравнены к 0.</summary>
    public List<TrafficDayUsage> DailyUsageLast(int days)
    {
        var snapshots = Load().OrderBy(s => s.DayNumber).ToList();
        var today = DateOnly.FromDateTime(DateTime.Now).DayNumber;
        var windowStart = today - (days - 1);
        int? firstSnapshotDay = snapshots.Count > 0 ? snapshots[0].DayNumber : null;

        var perDay = new Dictionary<int, long>();
        for (var i = 1; i < snapshots.Count; i++)
        {
            var prev = snapshots[i - 1];
            var curr = snapshots[i];
            var spanDays = curr.DayNumber - prev.DayNumber;
            if (spanDays <= 0) continue;
            var delta = Math.Max(curr.UsedBytes - prev.UsedBytes, 0L);
            var share = delta / spanDays;
            var remainder = delta % spanDays;
            for (var offset = 1; offset <= spanDays; offset++)
            {
                var day = prev.DayNumber + offset;
                if (day < windowStart) continue;
                // Последний день разрыва забирает остаток от целочисленного деления — сумма
                // распределённых долей в точности равна исходной дельте, ни один байт не теряется.
                perDay[day] = perDay.GetValueOrDefault(day) + share + (offset == spanDays ? remainder : 0);
            }
        }

        var result = new List<TrafficDayUsage>(days);
        for (var offset = days - 1; offset >= 0; offset--)
        {
            var day = today - offset;
            // День имеет посчитанную дельту, только если он строго позже самого первого снимка —
            // у самого первого снимка (baseline) и у всех более ранних дней нет "вчера", с
            // которым сравнивать.
            var hasData = firstSnapshotDay != null && day > firstSnapshotDay;
            result.Add(new TrafficDayUsage(DateOnly.FromDayNumber(day), perDay.GetValueOrDefault(day), hasData));
        }
        return result;
    }

    private static List<TrafficDaySnapshot> Load()
    {
        try { return File.Exists(StorePath) ? Decode(File.ReadAllText(StorePath)) : new List<TrafficDaySnapshot>(); }
        catch { return new List<TrafficDaySnapshot>(); }
    }
}
