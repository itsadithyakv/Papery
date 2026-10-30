using System.Text.Json.Serialization;
using Papery.Core.Viewing;

namespace Papery.Core.Storage;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Colour treatment of page content (separate from the app's own light/dark theme).</summary>
public enum ReadingMode
{
    Light,
    Dark,
    Sepia,
}

/// <summary>The user's preferences. Saved to settings.json in Papery's local data folder.</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public ReadingMode ReadingMode { get; set; } = ReadingMode.Light;

    public ViewMode DefaultViewMode { get; set; } = ViewMode.Continuous;

    public bool SidebarOpen { get; set; } = true;

    public bool RememberPosition { get; set; } = true;

    /// <summary>Never change an original: the first change in a tab asks where to save a copy.</summary>
    public bool AlwaysSaveCopy { get; set; }

    /// <summary>The "Getting started" tips card on the home screen (the user can hide it).</summary>
    public bool ShowGettingStarted { get; set; } = true;

    /// <summary>The one-time "Take a tour" note in the first document has been shown.</summary>
    public bool ViewerTourOffered { get; set; }

    /// <summary>Developer builds only: pretend Free / Pro. Release builds never read this.</summary>
    public Licensing.DebugLicenseMode DebugLicenseMode { get; set; }

    /// <summary>Window size in physical pixels when last closed (0 = never saved).</summary>
    public int WindowWidth { get; set; }

    public int WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }
}

/// <summary>How a file was last viewed, so it reopens at the same page and zoom.</summary>
public sealed class FileViewState
{
    public int Page { get; set; }

    /// <summary>Zoom factor, or 0 for "fit width".</summary>
    public double Zoom { get; set; }

    public ViewMode? ViewMode { get; set; }

    public DateTimeOffset LastOpened { get; set; }
}

/// <summary>Recent files and per-file positions. Saved to state.json.</summary>
public sealed class ViewStateData
{
    public List<string> RecentFiles { get; set; } = [];

    public Dictionary<string, FileViewState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ViewStateData))]
internal sealed partial class StateJsonContext : JsonSerializerContext;
