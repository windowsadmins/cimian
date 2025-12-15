using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Cimian.CLI.Repoclean.Services;

public class FileRepository : IFileRepository
{
    private readonly ILogger<FileRepository> _logger;
    private string _repositoryRoot = string.Empty;

    public FileRepository(ILogger<FileRepository> logger)
    {
        _logger = logger;
    }

    public void SetRepositoryRoot(string repositoryRoot)
    {
        _repositoryRoot = repositoryRoot;
    }

    public async Task<IEnumerable<string>> GetItemListAsync(string path)
    {
        var fullPath = Path.Combine(_repositoryRoot, path);
        
        if (!Directory.Exists(fullPath))
        {
            _logger.LogWarning("Directory does not exist: {Path}", fullPath);
            return Enumerable.Empty<string>();
        }

        try
        {
            return await Task.Run(() => Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(fullPath, f))
                .Where(f => !f.StartsWith(".")) // Skip hidden files
                .ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing items in {Path}", fullPath);
            throw;
        }
    }

    public async Task<string> GetContentAsync(string path)
    {
        var fullPath = Path.Combine(_repositoryRoot, path);
        
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found: {fullPath}");
        }

        try
        {
            return await File.ReadAllTextAsync(fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading file {Path}", fullPath);
            throw;
        }
    }

    public async Task DeleteAsync(string path)
    {
        var fullPath = Path.Combine(_repositoryRoot, path);
        
        try
        {
            if (File.Exists(fullPath))
            {
                await Task.Run(() => File.Delete(fullPath));
            }
            else if (Directory.Exists(fullPath))
            {
                await Task.Run(() => Directory.Delete(fullPath, true));
            }
            else
            {
                _logger.LogWarning("Path does not exist for deletion: {Path}", fullPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting {Path}", fullPath);
            throw;
        }
    }

    public bool Exists(string path)
    {
        var fullPath = string.IsNullOrEmpty(_repositoryRoot) ? path : Path.Combine(_repositoryRoot, path);
        return Directory.Exists(fullPath) || File.Exists(fullPath);
    }
}
