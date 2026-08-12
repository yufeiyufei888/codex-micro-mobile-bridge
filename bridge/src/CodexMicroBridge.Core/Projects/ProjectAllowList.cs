using CodexMicroBridge.Core.Persistence;

namespace CodexMicroBridge.Core.Projects;

public sealed class ProjectAllowList
{
    private readonly BridgeRepository _repository;

    public ProjectAllowList(BridgeRepository repository)
    {
        _repository = repository;
    }

    public async Task<AllowedProject> AddAsync(string path, CancellationToken cancellationToken = default)
    {
        var canonical = CanonicalizeExistingDirectory(path);
        var existing = (await _repository.GetAllowedProjectsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(project => string.Equals(project.Path, canonical, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var folderName = new DirectoryInfo(canonical).Name;
        var displayName = folderName.Length <= 200 ? folderName : folderName[..200];
        var project = new AllowedProject(Guid.NewGuid().ToString("N"), canonical, displayName);
        await _repository.AddAllowedProjectAsync(project, cancellationToken).ConfigureAwait(false);
        return project;
    }

    public Task RemoveAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return _repository.RemoveAllowedProjectAsync(projectId, cancellationToken);
    }

    public Task<IReadOnlyList<AllowedProject>> ListAsync(CancellationToken cancellationToken = default) =>
        _repository.GetAllowedProjectsAsync(cancellationToken);

    public async Task<AllowedProject> RequireProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return await _repository.GetAllowedProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("The projectId is not in the desktop project allow-list.");
    }

    public static bool IsSameOrDescendant(string candidate, string root)
    {
        var canonicalCandidate = Canonicalize(candidate);
        var canonicalRoot = Canonicalize(root);
        return string.Equals(canonicalCandidate, canonicalRoot, StringComparison.OrdinalIgnoreCase) ||
            canonicalCandidate.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string CanonicalizeExistingDirectory(string path)
    {
        var canonical = Canonicalize(path);
        if (!Directory.Exists(canonical))
        {
            throw new DirectoryNotFoundException($"Project directory '{canonical}' does not exist.");
        }

        return canonical;
    }

    private static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(fullPath) ? Path.GetPathRoot(path) ?? path : fullPath;
    }
}
