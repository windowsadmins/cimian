using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Cimian.Core.Msi;

/// <summary>
/// A read-only view of an MSI database, over msi.dll directly.
///
/// Cimian only ever reads MSI tables -- the Property table for identity and
/// the File, Component and Directory tables for the bill of materials -- so
/// this covers exactly that: open read-only, run a SELECT, fetch rows, read
/// string and integer fields. Values reach a query only as <c>?</c> parameters
/// bound from a record, never spliced into the SQL text.
/// </summary>
public sealed class MsiDatabase : IDisposable
{
    private readonly MsiHandle _handle;

    private MsiDatabase(MsiHandle handle) => _handle = handle;

    /// <summary>Opens <paramref name="path"/> read-only. Throws <see cref="Win32Exception"/> on failure.</summary>
    public static MsiDatabase OpenReadOnly(string path)
    {
        MsiNative.Check(MsiNative.MsiOpenDatabaseW(path, MsiNative.MSIDBOPEN_READONLY, out var handle));
        return new MsiDatabase(handle);
    }

    /// <summary>True when the database has a table named <paramref name="table"/>.</summary>
    public bool TableExists(string table)
    {
        var state = MsiNative.MsiDatabaseIsTablePersistentW(_handle, table);
        if (state == MsiNative.MSICONDITION_ERROR)
        {
            throw new Win32Exception($"Could not check MSI table '{table}'");
        }
        return state != MsiNative.MSICONDITION_NONE;
    }

    /// <summary>
    /// Opens and executes a SELECT. Each of <paramref name="parameters"/> binds,
    /// in order, to a <c>?</c> marker in <paramref name="sql"/>.
    /// </summary>
    public MsiView OpenView(string sql, params string[] parameters)
    {
        MsiNative.Check(MsiNative.MsiDatabaseOpenViewW(_handle, sql, out var viewHandle));
        var view = new MsiView(viewHandle);
        try
        {
            view.Execute(parameters);
            return view;
        }
        catch
        {
            view.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The first column of the first row the query returns, or null when it
    /// returns no rows.
    /// </summary>
    public string? ExecuteScalar(string sql, params string[] parameters)
    {
        using var view = OpenView(sql, parameters);
        using var record = view.Fetch();
        return record?.GetString(1);
    }

    /// <summary>The value of a Property table row, or null when the property is not set.</summary>
    public string? GetProperty(string name) =>
        ExecuteScalar("SELECT `Value` FROM `Property` WHERE `Property` = ?", name);

    public void Dispose() => _handle.Dispose();
}

/// <summary>An executed query over an <see cref="MsiDatabase"/>.</summary>
public sealed class MsiView : IDisposable
{
    private readonly MsiHandle _handle;

    internal MsiView(MsiHandle handle) => _handle = handle;

    internal void Execute(string[] parameters)
    {
        if (parameters.Length == 0)
        {
            MsiNative.Check(MsiNative.MsiViewExecute(_handle, IntPtr.Zero));
            return;
        }

        using var record = MsiNative.MsiCreateRecord((uint)parameters.Length);
        if (record.IsInvalid)
        {
            throw new Win32Exception("Could not create an MSI parameter record");
        }
        for (var i = 0; i < parameters.Length; i++)
        {
            MsiNative.Check(MsiNative.MsiRecordSetStringW(record, (uint)(i + 1), parameters[i]));
        }
        MsiNative.Check(MsiNative.MsiViewExecute(_handle, record.DangerousGetHandle()));
    }

    /// <summary>The next row, or null once every row has been read.</summary>
    public MsiRecord? Fetch()
    {
        var result = MsiNative.MsiViewFetch(_handle, out var record);
        if (result == MsiNative.ERROR_NO_MORE_ITEMS)
        {
            record.Dispose();
            return null;
        }
        MsiNative.Check(result);
        return new MsiRecord(record);
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>One row fetched from an <see cref="MsiView"/>. Fields are numbered from 1.</summary>
public sealed class MsiRecord : IDisposable
{
    private readonly MsiHandle _handle;

    internal MsiRecord(MsiHandle handle) => _handle = handle;

    /// <summary>The field as a string; a null field reads as the empty string.</summary>
    public string GetString(int field)
    {
        uint length = 256;
        var buffer = new StringBuilder((int)length);
        var result = MsiNative.MsiRecordGetStringW(_handle, (uint)field, buffer, ref length);
        if (result == MsiNative.ERROR_MORE_DATA)
        {
            // length now holds the size without the terminator.
            length++;
            buffer = new StringBuilder((int)length);
            result = MsiNative.MsiRecordGetStringW(_handle, (uint)field, buffer, ref length);
        }
        MsiNative.Check(result);
        return buffer.ToString();
    }

    /// <summary>The field as an integer; a null field reads as 0.</summary>
    public int GetInteger(int field)
    {
        var value = MsiNative.MsiRecordGetInteger(_handle, (uint)field);
        return value == MsiNative.MSI_NULL_INTEGER ? 0 : value;
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>An MSIHANDLE, closed with MsiCloseHandle.</summary>
internal sealed class MsiHandle : SafeHandle
{
    public MsiHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle() => MsiNative.MsiCloseHandle(handle) == 0;
}

internal static class MsiNative
{
    // MSIDBOPEN_READONLY is the persist-mode value 0, passed in place of the
    // output path string, which is how the API selects a mode.
    public static readonly IntPtr MSIDBOPEN_READONLY = IntPtr.Zero;

    public const uint ERROR_MORE_DATA = 234;
    public const uint ERROR_NO_MORE_ITEMS = 259;
    public const int MSI_NULL_INTEGER = int.MinValue;
    public const int MSICONDITION_NONE = 2;
    public const int MSICONDITION_ERROR = 3;

    public static void Check(uint result)
    {
        if (result != 0)
        {
            throw new Win32Exception((int)result);
        }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint MsiOpenDatabaseW(string databasePath, IntPtr persist, out MsiHandle database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int MsiDatabaseIsTablePersistentW(MsiHandle database, string tableName);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint MsiDatabaseOpenViewW(MsiHandle database, string query, out MsiHandle view);

    [DllImport("msi.dll", ExactSpelling = true)]
    public static extern uint MsiViewExecute(MsiHandle view, IntPtr record);

    [DllImport("msi.dll", ExactSpelling = true)]
    public static extern uint MsiViewFetch(MsiHandle view, out MsiHandle record);

    [DllImport("msi.dll", ExactSpelling = true)]
    public static extern MsiHandle MsiCreateRecord(uint parameterCount);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint MsiRecordSetStringW(MsiHandle record, uint field, string value);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint MsiRecordGetStringW(MsiHandle record, uint field, StringBuilder value, ref uint length);

    [DllImport("msi.dll", ExactSpelling = true)]
    public static extern int MsiRecordGetInteger(MsiHandle record, uint field);

    [DllImport("msi.dll", ExactSpelling = true)]
    public static extern uint MsiCloseHandle(IntPtr handle);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint MsiEnumRelatedProductsW(string upgradeCode, uint reserved, uint index, StringBuilder productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint MsiGetProductInfoW(string productCode, string property, StringBuilder value, ref uint length);
}
