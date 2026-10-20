using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TodoSticker;

/// <summary>把 DateTimeOffset 转成中文相对时间：刚刚 / 12 分钟前 / 今天 14:20 / 昨天 09:10 / 10月12日 14:20 / 2024年…。</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset time)
        {
            return string.Empty;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset local = time.ToLocalTime();
        TimeSpan delta = now - local;

        if (delta.TotalSeconds < 0)
        {
            return local.ToString("M月d日 HH:mm", culture);
        }

        if (delta.TotalSeconds < 60)
        {
            return "刚刚";
        }

        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes} 分钟前";
        }

        if (local.Date == now.Date)
        {
            return $"今天 {local:HH:mm}";
        }

        if (local.Date == now.Date.AddDays(-1))
        {
            return $"昨天 {local:HH:mm}";
        }

        if (local.Year == now.Year)
        {
            return local.ToString("M月d日 HH:mm", culture);
        }

        return local.ToString("yyyy年M月d日", culture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
