using System.Security.AccessControl;
using System.Security.Principal;

namespace Cimian.Tests.Shared;

/// <summary>Sets the specific owner a security test requires.</summary>
internal static class TestFileOwner
{
    /// <summary>Gives <paramref name="path"/> to the account running the tests, never to a group.</summary>
    public static void SetToCurrentUser(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        info.SetAccessControl(security);
    }

}
