#include "profiler/RetentionProfilerProtocol.h"

#include <cassert>
#include <cstring>
#include <cwchar>

/// <summary>
/// 验证 Controller 只接受版本匹配、容量有效且以空字符终止的附加协议。
/// </summary>
int main()
{
    static_assert(sizeof(DotnetAnalysis::RetentionProfilerSharedHeader) == 88);
    static_assert(sizeof(DotnetAnalysis::RetentionProfilerObjectRecord) == 24);
    static_assert(sizeof(DotnetAnalysis::RetentionProfilerTypeEvidenceRecord) == 1552);

    DotnetAnalysis::RetentionProfilerAttachData attachData{};
    attachData.version = DotnetAnalysis::kRetentionProfilerProtocolVersion;
    attachData.mappingCapacityBytes = 64 * 1024;
    std::wmemcpy(attachData.mappingName, L"DotnetAnalysis.Retention.Test", 29);
    std::wmemcpy(attachData.completionEventName, L"DotnetAnalysis.Retention.Complete", 31);
    std::wmemcpy(attachData.failureEventName, L"DotnetAnalysis.Retention.Failed", 30);
    std::wmemcpy(attachData.detachEventName, L"DotnetAnalysis.Retention.Detached", 32);

    assert(DotnetAnalysis::IsValidRetentionProfilerAttachData(attachData));

    attachData.mappingCapacityBytes = 0;
    assert(!DotnetAnalysis::IsValidRetentionProfilerAttachData(attachData));
    return 0;
}
