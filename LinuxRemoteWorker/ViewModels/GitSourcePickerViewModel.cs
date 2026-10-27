using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LinuxRemoteWorker.Core;
using LinuxRemoteWorker.Modules.Repositories;

namespace LinuxRemoteWorker.ViewModels;

/// <summary>
/// The "what to check out" controls used by the clone form, the switch panel and the service version panel:
/// a kind (branch, tag, latest release, commit), a value with suggestions from the repository, and options.
/// </summary>
public partial class GitSourcePickerViewModel : ObservableObject
{
    public const string DefaultBranchKind = "Default branch";
    public const string BranchKind = "Branch";
    public const string TagKind = "Tag";
    public const string LatestReleaseKind = "Latest release";
    public const string CommitKind = "Commit";

    private RemoteRefs? _refs;

    public GitSourcePickerViewModel(bool allowDefaultBranch)
    {
        Kinds = allowDefaultBranch
            ? [DefaultBranchKind, BranchKind, TagKind, LatestReleaseKind, CommitKind]
            : [BranchKind, TagKind, LatestReleaseKind, CommitKind];
        _kind = Kinds[0];
    }

    public IReadOnlyList<string> Kinds { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueLabel), nameof(HasValue), nameof(IsLatestRelease))]
    private string _kind;

    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private bool _includePrereleases;
    [ObservableProperty] private string _refsSummary = string.Empty;

    /// <summary>Suggestions for <see cref="Value"/>: branches or tags (newest first) once loaded.</summary>
    public ObservableCollection<string> Options { get; } = [];

    public string ValueLabel => Kind switch
    {
        BranchKind => "Branch",
        TagKind => "Tag",
        LatestReleaseKind => "Tag pattern (* = any)",
        CommitKind => "Commit hash",
        _ => string.Empty
    };

    public bool HasValue => Kind != DefaultBranchKind;

    public bool IsLatestRelease => Kind == LatestReleaseKind;

    partial void OnKindChanged(string value)
    {
        FillOptions();
        // A sensible starting value per kind; the user can still type anything
        Value = value switch
        {
            BranchKind => _refs?.DefaultBranch ?? string.Empty,
            TagKind => _refs?.LatestRelease?.Name ?? _refs?.Tags.FirstOrDefault()?.Name ?? string.Empty,
            LatestReleaseKind => GitSource.AnyTag,
            _ => string.Empty
        };
    }

    /// <summary>Shows the branches and tags of the repository as suggestions.</summary>
    public void Load(RemoteRefs refs)
    {
        _refs = refs;
        var release = refs.LatestRelease == null ? "no release tags" : $"latest release {refs.LatestRelease.Name}";
        var pre = refs.LatestPrerelease == null ? "" : $", newer pre-release {refs.LatestPrerelease.Name}";
        RefsSummary = $"Default branch {refs.DefaultBranch ?? "-"} · {refs.Branches.Count} branches · {refs.Tags.Count} tags · {release}{pre}";
        if (string.IsNullOrWhiteSpace(Value)) OnKindChanged(Kind);
        else FillOptions();
    }

    /// <summary>Starts from what a clone follows now.</summary>
    public void Preset(GitSource? source)
    {
        if (source == null) return;
        Kind = source.Kind switch
        {
            GitSourceKind.Branch => BranchKind,
            GitSourceKind.Tag => TagKind,
            GitSourceKind.Commit => CommitKind,
            _ => LatestReleaseKind
        };
        Value = source.Value;
        IncludePrereleases = source.IncludePrereleases;
    }

    public void Reset()
    {
        _refs = null;
        RefsSummary = string.Empty;
        IncludePrereleases = false;
        Kind = Kinds[0];
        Value = string.Empty;
        Options.Clear();
    }

    /// <summary>The chosen source; null for the default branch. Throws <see cref="OperationException"/> for a missing or invalid value.</summary>
    public GitSource? Build()
    {
        var source = Kind switch
        {
            DefaultBranchKind => null,
            BranchKind => GitSource.Branch(Required("a branch")),
            TagKind => GitSource.Tag(Required("a tag")),
            CommitKind => GitSource.Commit(Required("a commit hash")),
            _ => GitSource.LatestRelease(Value, IncludePrereleases)
        };
        OperationException.ThrowIfInvalid(source?.Validate());
        return source;
    }

    private string Required(string what) =>
        string.IsNullOrWhiteSpace(Value) ? throw OperationException.Invalid($"Enter {what}.") : Value.Trim();

    private void FillOptions()
    {
        // An editable ComboBox clears its text when the selected suggestion is removed - keep what was entered
        var value = Value;
        Options.Clear();
        IEnumerable<string> names = Kind switch
        {
            BranchKind => _refs?.Branches.Select(b => b.Name) ?? [],
            TagKind => _refs?.Tags.Select(t => t.Name) ?? [],
            LatestReleaseKind => [GitSource.AnyTag, "v*"],
            _ => []
        };
        foreach (var name in names) Options.Add(name);
        Value = value;
    }
}
