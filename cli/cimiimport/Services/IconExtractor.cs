using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cimian.CLI.Cimiimport.Services;

/// <summary>
/// Extracts icons from Windows installer files (EXE, MSI, MSIX, NUPKG) and converts to PNG.
/// Based on Munki's iconutils approach adapted for Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public class IconExtractor
{
    // Windows Shell32 API for icon extraction
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    private const int DESIRED_ICON_SIZE = 256; // Target PNG size in pixels

    /// <summary>
    /// Extracts an icon from an installer file and saves it as PNG.
    /// </summary>
    /// <param name="installerPath">Path to the installer (EXE, MSI, MSIX, or NUPKG)</param>
    /// <param name="repoPath">Path to the Cimian repository</param>
    /// <param name="packageName">Package name for icon naming</param>
    /// <param name="customOutputPath">Optional custom output path (overrides repo icons folder)</param>
    /// <returns>Icon name (filename) if successful, or null if extraction failed</returns>
    public string? ExtractIconToPng(string installerPath, string repoPath, string packageName, string? customOutputPath = null)
    {
        var extension = Path.GetExtension(installerPath).ToLowerInvariant();

        // Determine output path - either custom or repo icons folder
        string outputPath;
        if (!string.IsNullOrEmpty(customOutputPath))
        {
            outputPath = customOutputPath;
        }
        else
        {
            var iconsFolder = Path.Combine(repoPath, "icons");
            Directory.CreateDirectory(iconsFolder);
            outputPath = Path.Combine(iconsFolder, $"{packageName}.png");
        }

        try
        {
            var result = extension switch
            {
                ".exe" => ExtractFromExe(installerPath, outputPath, packageName),
                ".msi" => ExtractFromMsi(installerPath, outputPath, packageName),
                ".msix" or ".appx" => ExtractFromMsix(installerPath, outputPath, packageName),
                ".nupkg" => ExtractFromNupkg(installerPath, outputPath, packageName),
                _ => null
            };

            // Return just the filename (icon_name field), not full path
            return result != null ? Path.GetFileName(result) : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Icon extraction failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extracts icon from an EXE file using Windows Shell API.
    /// </summary>
    private string? ExtractFromExe(string exePath, string? outputPath, string packageName)
    {
        Console.WriteLine("[INFO] Extracting icon from EXE...");

        // Try to extract large icons (up to 256x256 on modern Windows)
        var largeIcons = new IntPtr[1];
        var smallIcons = new IntPtr[1];

        uint count = ExtractIconEx(exePath, 0, largeIcons, smallIcons, 1);
        if (count == 0 || largeIcons[0] == IntPtr.Zero)
        {
            // Fallback to ExtractIcon
            var hIcon = ExtractIcon(IntPtr.Zero, exePath, 0);
            if (hIcon == IntPtr.Zero || hIcon.ToInt64() == 1)
            {
                Console.WriteLine("   No icon found in EXE");
                return null;
            }
            largeIcons[0] = hIcon;
        }

        try
        {
            using var icon = Icon.FromHandle(largeIcons[0]);
            return SaveIconAsPng(icon, outputPath, packageName);
        }
        finally
        {
            if (largeIcons[0] != IntPtr.Zero)
                DestroyIcon(largeIcons[0]);
            if (smallIcons[0] != IntPtr.Zero)
                DestroyIcon(smallIcons[0]);
        }
    }

    /// <summary>
    /// Extracts icon from an MSI file by querying its Icon table or extracting from embedded streams.
    /// </summary>
    private string? ExtractFromMsi(string msiPath, string? outputPath, string packageName)
    {
        Console.WriteLine("[INFO] Extracting icon from MSI...");

        // Method 1: Try to find icon in the Icon table using msiexec
        var tempDir = Path.Combine(Path.GetTempPath(), $"cimian_icon_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Use PowerShell to extract icon from MSI
            var script = $@"
$msiPath = '{msiPath.Replace("'", "''")}'
$tempDir = '{tempDir.Replace("'", "''")}'

try {{
    $windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
    $database = $windowsInstaller.OpenDatabase($msiPath, 0)
    
    # Query the Icon table
    $view = $database.OpenView(""SELECT Name, Data FROM Icon"")
    $view.Execute()
    
    $record = $view.Fetch()
    if ($record) {{
        $iconName = $record.StringData(1)
        $iconPath = Join-Path $tempDir ""$iconName""
        
        # Write icon data to file
        $record.SetStream(2, $iconPath)
        Write-Output $iconPath
    }}
    
    $view.Close()
    [System.Runtime.Interopservices.Marshal]::ReleaseComObject($database) | Out-Null
    [System.Runtime.Interopservices.Marshal]::ReleaseComObject($windowsInstaller) | Out-Null
}} catch {{
    Write-Error $_.Exception.Message
}}
";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            var output = process?.StandardOutput.ReadToEnd().Trim();
            var error = process?.StandardError.ReadToEnd();
            process?.WaitForExit();

            if (!string.IsNullOrEmpty(output) && File.Exists(output))
            {
                // Convert ICO to PNG
                return ConvertIcoToPng(output, outputPath, packageName);
            }

            // Method 2: Fallback - check if MSI has ARPPRODUCTICON property pointing to exe
            Console.WriteLine("   No icon in MSI Icon table, checking for installed application...");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   MSI icon extraction error: {ex.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Extracts icon from an MSIX/APPX package by reading the AppxManifest.xml.
    /// </summary>
    private string? ExtractFromMsix(string msixPath, string? outputPath, string packageName)
    {
        Console.WriteLine("[INFO] Extracting icon from MSIX/APPX...");

        var tempDir = Path.Combine(Path.GetTempPath(), $"cimian_msix_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Extract the MSIX package
            System.IO.Compression.ZipFile.ExtractToDirectory(msixPath, tempDir);

            // Read AppxManifest.xml to find the logo path
            var manifestPath = Path.Combine(tempDir, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Console.WriteLine("   AppxManifest.xml not found");
                return null;
            }

            var manifestContent = File.ReadAllText(manifestPath);
            
            // Parse for Logo or Square150x150Logo or Square44x44Logo
            var logoPatterns = new[]
            {
                @"Square310x310Logo=""([^""]+)""",
                @"Square150x150Logo=""([^""]+)""",
                @"Square44x44Logo=""([^""]+)""",
                @"Logo=""([^""]+)"""
            };

            foreach (var pattern in logoPatterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(manifestContent, pattern);
                if (match.Success)
                {
                    var logoRelativePath = match.Groups[1].Value;
                    
                    // MSIX logos often have scale variants - find the largest
                    var logoDir = Path.GetDirectoryName(Path.Combine(tempDir, logoRelativePath)) ?? tempDir;
                    var logoBaseName = Path.GetFileNameWithoutExtension(logoRelativePath);
                    var logoExt = Path.GetExtension(logoRelativePath);

                    // Look for scale-400, scale-200, scale-150, scale-100 variants
                    var scales = new[] { "scale-400", "scale-200", "scale-150", "scale-125", "scale-100", "" };
                    foreach (var scale in scales)
                    {
                        var scaledName = string.IsNullOrEmpty(scale) 
                            ? $"{logoBaseName}{logoExt}"
                            : $"{logoBaseName}.{scale}{logoExt}";
                        
                        var logoFullPath = Path.Combine(logoDir, scaledName);
                        if (File.Exists(logoFullPath))
                        {
                            return ConvertImageToPng(logoFullPath, outputPath, packageName);
                        }
                    }

                    // Try the exact path
                    var exactPath = Path.Combine(tempDir, logoRelativePath);
                    if (File.Exists(exactPath))
                    {
                        return ConvertImageToPng(exactPath, outputPath, packageName);
                    }
                }
            }

            Console.WriteLine("   No logo found in MSIX manifest");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   MSIX icon extraction error: {ex.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Extracts icon from a NuGet package (.nupkg) by reading the nuspec or looking for icon files.
    /// </summary>
    private string? ExtractFromNupkg(string nupkgPath, string? outputPath, string packageName)
    {
        Console.WriteLine("[INFO] Extracting icon from NUPKG...");

        var tempDir = Path.Combine(Path.GetTempPath(), $"cimian_nupkg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(nupkgPath, tempDir);

            // Look for icon file specified in nuspec
            var nuspecFiles = Directory.GetFiles(tempDir, "*.nuspec");
            if (nuspecFiles.Length > 0)
            {
                var nuspecContent = File.ReadAllText(nuspecFiles[0]);
                
                // Check for <icon> element (NuGet 5.3+)
                var iconMatch = System.Text.RegularExpressions.Regex.Match(nuspecContent, @"<icon>([^<]+)</icon>");
                if (iconMatch.Success)
                {
                    var iconRelPath = iconMatch.Groups[1].Value;
                    var iconFullPath = Path.Combine(tempDir, iconRelPath);
                    if (File.Exists(iconFullPath))
                    {
                        return ConvertImageToPng(iconFullPath, outputPath, packageName);
                    }
                }

                // Check for <iconUrl> element (legacy)
                var iconUrlMatch = System.Text.RegularExpressions.Regex.Match(nuspecContent, @"<iconUrl>([^<]+)</iconUrl>");
                if (iconUrlMatch.Success)
                {
                    Console.WriteLine($"   Package has iconUrl: {iconUrlMatch.Groups[1].Value}");
                    Console.WriteLine("   (URL-based icons not supported, would require download)");
                }
            }

            // Fallback: look for common icon file patterns
            var iconPatterns = new[] { "icon.png", "icon.ico", "*.icon.png", "images/icon.png" };
            foreach (var pattern in iconPatterns)
            {
                var matches = Directory.GetFiles(tempDir, pattern, SearchOption.AllDirectories);
                if (matches.Length > 0)
                {
                    return ConvertImageToPng(matches[0], outputPath, packageName);
                }
            }

            Console.WriteLine("   No icon found in NUPKG");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   NUPKG icon extraction error: {ex.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Converts an ICO file to PNG, selecting the largest available size.
    /// </summary>
    private string? ConvertIcoToPng(string icoPath, string? outputPath, string packageName)
    {
        try
        {
            using var icon = new Icon(icoPath, DESIRED_ICON_SIZE, DESIRED_ICON_SIZE);
            return SaveIconAsPng(icon, outputPath, packageName);
        }
        catch
        {
            // Fallback: try loading as any size
            try
            {
                using var icon = new Icon(icoPath);
                return SaveIconAsPng(icon, outputPath, packageName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   Failed to convert ICO: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Converts any image file to PNG format.
    /// </summary>
    private string? ConvertImageToPng(string imagePath, string? outputPath, string packageName)
    {
        try
        {
            using var image = Image.FromFile(imagePath);
            
            var finalPath = GetOutputPath(outputPath, packageName);
            
            // Resize if needed to standard size
            if (image.Width != DESIRED_ICON_SIZE || image.Height != DESIRED_ICON_SIZE)
            {
                using var resized = new Bitmap(DESIRED_ICON_SIZE, DESIRED_ICON_SIZE);
                using var graphics = Graphics.FromImage(resized);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                
                // Calculate scaling to fit while preserving aspect ratio
                var scale = Math.Min((float)DESIRED_ICON_SIZE / image.Width, (float)DESIRED_ICON_SIZE / image.Height);
                var newWidth = (int)(image.Width * scale);
                var newHeight = (int)(image.Height * scale);
                var x = (DESIRED_ICON_SIZE - newWidth) / 2;
                var y = (DESIRED_ICON_SIZE - newHeight) / 2;
                
                graphics.DrawImage(image, x, y, newWidth, newHeight);
                resized.Save(finalPath, ImageFormat.Png);
            }
            else
            {
                image.Save(finalPath, ImageFormat.Png);
            }

            Console.WriteLine($"   [OK] Icon saved to: {finalPath}");
            return finalPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   Failed to convert image: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Saves a System.Drawing.Icon as PNG.
    /// </summary>
    private string? SaveIconAsPng(Icon icon, string? outputPath, string packageName)
    {
        try
        {
            var finalPath = GetOutputPath(outputPath, packageName);
            
            using var bitmap = icon.ToBitmap();
            
            // Resize to standard size if needed
            if (bitmap.Width != DESIRED_ICON_SIZE || bitmap.Height != DESIRED_ICON_SIZE)
            {
                using var resized = new Bitmap(DESIRED_ICON_SIZE, DESIRED_ICON_SIZE);
                using var graphics = Graphics.FromImage(resized);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.DrawImage(bitmap, 0, 0, DESIRED_ICON_SIZE, DESIRED_ICON_SIZE);
                resized.Save(finalPath, ImageFormat.Png);
            }
            else
            {
                bitmap.Save(finalPath, ImageFormat.Png);
            }

            Console.WriteLine($"   [OK] Icon saved to: {finalPath}");
            return finalPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   Failed to save icon as PNG: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gets the output path for the PNG file.
    /// </summary>
    private static string GetOutputPath(string? outputPath, string packageName)
    {
        if (!string.IsNullOrEmpty(outputPath))
        {
            // If outputPath is a directory, append the package name
            if (Directory.Exists(outputPath))
            {
                return Path.Combine(outputPath, $"{packageName}.png");
            }
            return outputPath;
        }

        // Default to temp directory
        return Path.Combine(Path.GetTempPath(), $"{packageName}.png");
    }

    /// <summary>
    /// Copies the extracted icon to the repo's icons directory.
    /// </summary>
    public string? CopyToRepo(string iconPath, string repoPath, string packageName)
    {
        try
        {
            var iconsDir = Path.Combine(repoPath, "icons");
            Directory.CreateDirectory(iconsDir);

            var destPath = Path.Combine(iconsDir, $"{packageName}.png");
            File.Copy(iconPath, destPath, overwrite: true);

            Console.WriteLine($"[INFO] Icon copied to repo: {destPath}");
            return $"{packageName}.png";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Failed to copy icon to repo: {ex.Message}");
            return null;
        }
    }
}
