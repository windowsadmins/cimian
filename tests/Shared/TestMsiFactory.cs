using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cimian.Tests.Shared;

/// <summary>
/// Builds real MSI databases on disk for tests, through msi.dll. Production
/// code only ever opens MSIs read-only, so creating them lives here.
/// Every file created is deleted on Dispose.
/// </summary>
public sealed class TestMsiFactory : IDisposable
{
    private const int MSIDBOPEN_CREATE = 3;

    private readonly List<string> _paths = new();

    /// <summary>Creates an MSI, runs each SQL statement against it, commits it and returns its path.</summary>
    public string Create(params TestMsiSql[] statements)
    {
        var path = Path.Combine(Path.GetTempPath(), $"msitest_{Guid.NewGuid():N}.msi");
        _paths.Add(path);

        Check(MsiOpenDatabaseW(path, MSIDBOPEN_CREATE, out var db));
        try
        {
            foreach (var sql in statements)
            {
                Check(MsiDatabaseOpenViewW(db, sql.Text, out var view));
                var record = IntPtr.Zero;
                try
                {
                    if (sql.Args.Length > 0)
                    {
                        record = MsiCreateRecord((uint)sql.Args.Length);
                        for (var i = 0; i < sql.Args.Length; i++)
                        {
                            Check(MsiRecordSetStringW(record, (uint)(i + 1), sql.Args[i]));
                        }
                    }
                    Check(MsiViewExecute(view, record));
                }
                finally
                {
                    if (record != IntPtr.Zero) MsiCloseHandle(record);
                    MsiCloseHandle(view);
                }
            }
            Check(MsiDatabaseCommit(db));
        }
        finally
        {
            MsiCloseHandle(db);
        }

        return path;
    }

    public void Dispose()
    {
        foreach (var path in _paths)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static void Check(uint result)
    {
        if (result != 0)
        {
            throw new Win32Exception((int)result);
        }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr db);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiDatabaseOpenViewW(IntPtr db, string query, out IntPtr view);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern IntPtr MsiCreateRecord(uint count);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiRecordSetStringW(IntPtr record, uint field, string value);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiDatabaseCommit(IntPtr db);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiCloseHandle(IntPtr handle);
}

/// <summary>
/// One statement for <see cref="TestMsiFactory.Create"/>. A plain string
/// converts to a statement with no arguments; values MSI SQL cannot spell as a
/// literal (an apostrophe, a long string) go in as <c>?</c> arguments.
/// </summary>
public sealed record TestMsiSql(string Text, params string[] Args)
{
    public static implicit operator TestMsiSql(string text) => new(text);
}
