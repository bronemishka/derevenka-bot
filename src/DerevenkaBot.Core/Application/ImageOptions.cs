namespace DerevenkaBot.Core.Application;

public sealed class ImageOptions
{
    public bool Enabled { get; set; }
    public bool UseFakeProvider { get; set; } = true;
    public bool LimitsEnabled { get; set; }
    public string DefaultModel { get; set; } = "gpt-image-2.6-flare";
    public string HighQualityModel { get; set; } = "gpt-image-2.6-sunburst";
    public string DefaultQuality { get; set; } = "medium";
    public string DefaultSize { get; set; } = "1024x1024";
    public string? Complexity { get; set; }
    public string StoragePath { get; set; } = "./data/image-generation";
    public int RetentionDays { get; set; } = 30;
    public int DraftMinutes { get; set; } = 15;
    public int MaxInputImages { get; set; } = 4;
    public int MaxInputFileMb { get; set; } = 15;
    public long MaxPixels { get; set; } = 40_000_000;
    public int MaxConcurrentJobs { get; set; } = 2;
    public int MaxConcurrentJobsPerUser { get; set; } = 1;
    public int MaxPendingJobs { get; set; } = 30;
    public int MaxAttempts { get; set; } = 3;
    public int LeaseSeconds { get; set; } = 120;
    public int DailyJobsPerUser { get; set; } = 5;
    public int GlobalDailyJobs { get; set; } = 30;
    public int GlobalMonthlyJobs { get; set; } = 300;
    public decimal ReservationUsd { get; set; } = 0.25m;
    public decimal UserDailyBudgetUsd { get; set; } = 1.25m;
    public decimal BotDailyBudgetUsd { get; set; } = 7.5m;
    public decimal BotMonthlyBudgetUsd { get; set; } = 75m;
    public string PricingVersion { get; set; } = "manual-estimate-2026-11";
    public decimal? InputTextUsdPerMillion { get; set; }
    public decimal? InputImageUsdPerMillion { get; set; }
    public decimal? OutputImageUsdPerMillion { get; set; }
    public string CounterTimeZone { get; set; } = "UTC";
    public long[] AllowedUsers { get; set; } = [];
    public long[] AllowedChats { get; set; } = [];
    public long OwnerUserId { get; set; }
    public string SafetySalt { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public void Validate()
    {
        var errors = new List<string>();
        void Check(bool valid, string name, object value, string expected)
        {
            if (!valid) errors.Add($"{name}={Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)} (допустимо {expected})");
        }
        Check(MaxConcurrentJobs is >= 1 and <= 16, nameof(MaxConcurrentJobs), MaxConcurrentJobs, "от 1 до 16");
        Check(MaxConcurrentJobsPerUser >= 1, nameof(MaxConcurrentJobsPerUser), MaxConcurrentJobsPerUser, "от 1");
        Check(MaxInputFileMb is >= 1 and <= 20, nameof(MaxInputFileMb), MaxInputFileMb, "от 1 до 20");
        Check(MaxPixels >= 1, nameof(MaxPixels), MaxPixels, "от 1");
        Check(MaxInputImages is >= 1 and <= 4, nameof(MaxInputImages), MaxInputImages, "от 1 до 4");
        Check(LeaseSeconds >= 15, nameof(LeaseSeconds), LeaseSeconds, "от 15");
        Check(MaxAttempts >= 1, nameof(MaxAttempts), MaxAttempts, "от 1");
        Check(RetentionDays >= 1, nameof(RetentionDays), RetentionDays, "от 1");
        Check(DraftMinutes >= 1, nameof(DraftMinutes), DraftMinutes, "от 1");
        Check(ReservationUsd >= 0, nameof(ReservationUsd), ReservationUsd, "от 0");
        if (errors.Count > 0)
            throw new InvalidOperationException("Некорректные лимиты ImageGeneration: " + string.Join("; ", errors) + ".");
        if (LimitsEnabled && (DailyJobsPerUser < 1 || GlobalDailyJobs < 1 || GlobalMonthlyJobs < 1 ||
            MaxPendingJobs < 1 || UserDailyBudgetUsd <= 0 || BotDailyBudgetUsd <= 0 || BotMonthlyBudgetUsd <= 0))
            throw new InvalidOperationException("Некорректные квоты ImageGeneration. Отключите LimitsEnabled для работы без ограничений.");
        if (string.IsNullOrWhiteSpace(DefaultModel) ||
            !new[] { "low", "medium", "high", "xhigh", "max" }.Contains(DefaultQuality) ||
            !new[] { "1024x1024", "1536x1024", "1024x1536" }.Contains(DefaultSize))
            throw new InvalidOperationException("Проверьте модель, качество и размер ImageGeneration.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(CounterTimeZone);
        if (!UseFakeProvider && string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("Для OpenAI укажите ImageGeneration:ApiKey или OPENAI_API_KEY.");
        if (!UseFakeProvider && !string.IsNullOrWhiteSpace(SafetySalt) && SafetySalt.Length < 16)
            throw new InvalidOperationException("SafetySalt должен содержать от 16 символов. Оставьте его пустым для автоматической генерации.");
    }
}
