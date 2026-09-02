using DotnetAnalysis.Core.Sessions;

namespace DotnetAnalysis.Application.Contracts;

public interface IAnalysisService
{
    Task AnalyzeAsync(AnalysisSession session, CancellationToken cancellationToken);
}
