namespace WPAIPoster.Ui;

/// <summary>Console verbosity level. The log file always captures full detail regardless of this.</summary>
public enum Verbosity
{
    /// <summary>Only warnings, errors, and the final result.</summary>
    Quiet,

    /// <summary>Default: stage status, progress, and the rendered post.</summary>
    Normal,

    /// <summary>Everything, including dim detail lines and raw model I/O.</summary>
    Verbose,
}
