namespace RTXSandbox.Gameplay;

public sealed class WorldClock
{
    public float TimeOfDayHours { get; private set; }
    public float DayLengthMinutes { get; private set; }
    public bool Paused { get; set; }

    public string DisplayTime
    {
        get
        {
            int totalMinutes = (int)MathF.Floor(TimeOfDayHours * 60f + 0.5f) % (24 * 60);
            return $"{totalMinutes / 60:00}:{totalMinutes % 60:00}";
        }
    }

    public WorldClock(float startHour, float dayLengthMinutes, bool paused = false)
    {
        SetTime(startHour);
        SetDayLength(dayLengthMinutes);
        Paused = paused;
    }

    public void Update(float realDeltaSeconds)
    {
        if (!Paused)
        {
            AdjustHours(realDeltaSeconds * 24f / (DayLengthMinutes * 60f));
        }
    }

    public void AdjustHours(float hours) => SetTime(TimeOfDayHours + hours);

    public void SetTime(float hour) => TimeOfDayHours = ((hour % 24f) + 24f) % 24f;

    public void SetDayLength(float minutes) => DayLengthMinutes = Math.Clamp(minutes, 1f, 120f);

    public void MultiplySpeed(float multiplier)
    {
        if (multiplier > 0f)
        {
            SetDayLength(DayLengthMinutes / multiplier);
        }
    }

    public float CircularDistanceFrom(float otherHour)
    {
        float difference = MathF.Abs(TimeOfDayHours - otherHour);
        return MathF.Min(difference, 24f - difference);
    }
}
