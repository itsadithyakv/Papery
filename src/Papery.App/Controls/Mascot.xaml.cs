using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Papery.Services;

namespace Papery.Controls;

public enum MascotMood
{
    Happy,
    Excited,
    Oops,
    Sleepy,
}

/// <summary>The Papery character. Floats gently and blinks now and then; change <see cref="Mood"/> to react.</summary>
public sealed partial class Mascot : UserControl
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(MascotMood), typeof(Mascot), new PropertyMetadata(MascotMood.Happy, (d, _) => ((Mascot)d).ApplyMood()));

    public static readonly DependencyProperty IsFloatingProperty = DependencyProperty.Register(
        nameof(IsFloating), typeof(bool), typeof(Mascot), new PropertyMetadata(true));

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _blinkTimer;

    public Mascot()
    {
        InitializeComponent();
        _blinkTimer = DispatcherQueue.CreateTimer();
        _blinkTimer.IsRepeating = false;
        _blinkTimer.Tick += (_, _) =>
        {
            if (Mood is MascotMood.Happy or MascotMood.Oops)
            {
                Motion.Blink(OpenEyes, new Vector3(32, 34, 0));
            }

            ScheduleBlink();
        };

        Loaded += (_, _) =>
        {
            if (IsFloating)
            {
                Motion.Float(Body);
            }

            ScheduleBlink();
        };
        Unloaded += (_, _) => _blinkTimer.Stop();
        ApplyMood();
    }

    public MascotMood Mood
    {
        get => (MascotMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    public bool IsFloating
    {
        get => (bool)GetValue(IsFloatingProperty);
        set => SetValue(IsFloatingProperty, value);
    }

    /// <summary>A happy little bounce.</summary>
    public void Bounce() => Motion.Pop(this, 1.15f);

    private void ScheduleBlink()
    {
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(Random.Shared.Next(2800, 6000));
        _blinkTimer.Start();
    }

    private void ApplyMood()
    {
        var mood = Mood;
        OpenEyes.Visibility = mood is MascotMood.Happy or MascotMood.Oops ? Visibility.Visible : Visibility.Collapsed;
        HappyEyes.Visibility = mood == MascotMood.Excited ? Visibility.Visible : Visibility.Collapsed;
        SleepyEyes.Visibility = mood == MascotMood.Sleepy ? Visibility.Visible : Visibility.Collapsed;
        SmileMouth.Visibility = mood == MascotMood.Happy ? Visibility.Visible : Visibility.Collapsed;
        ExcitedMouth.Visibility = mood == MascotMood.Excited ? Visibility.Visible : Visibility.Collapsed;
        OopsMouth.Visibility = mood == MascotMood.Oops ? Visibility.Visible : Visibility.Collapsed;
        SleepyMouth.Visibility = mood == MascotMood.Sleepy ? Visibility.Visible : Visibility.Collapsed;
    }
}
