#include "profiler/RetentionProfilerProtocol.h"

#include <cassert>
#include <cstdio>
#include <cstring>
#include <cwchar>

namespace
{
    /// <summary>
    /// 确定性复现 writer 已登记后 publisher 抢先封存的交错；失败 claimant 必须完成最后一次发布转换。
    /// </summary>
    bool FailedWriterClaimPublishesSealedGeneration()
    {
        DotnetAnalysis::RetentionProfilerSharedHeader header{};
        header.segmentState = static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Writing);
        header.edgeCapacity = 1;
        const LONGLONG observedPublicationSequence = InterlockedCompareExchange64(
            &header.publicationSequence,
            0,
            0);
        if (InterlockedCompareExchange(
                &header.segmentState,
                static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Writing),
                static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Writing))
            != static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Writing)
            || !DotnetAnalysis::RetentionProfilerProtocolDetail::TryRegisterRetentionProfilerSegmentWriter(&header))
        {
            return false;
        }

        LONGLONG nextPublicationSequence{};
        if (!DotnetAnalysis::TryPublishRetentionProfilerSegment(&header, &nextPublicationSequence)
            || header.segmentState != static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Sealing)
            || DotnetAnalysis::RetentionProfilerProtocolDetail::ConfirmRetentionProfilerSegmentWriter(
                &header,
                observedPublicationSequence)
            || header.segmentState != static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Published)
            || !DotnetAnalysis::TryAcknowledgeRetentionProfilerSegment(&header, 1))
        {
            return false;
        }

        DotnetAnalysis::RetentionProfilerSharedHeader* segments[]{ &header };
        std::uint32_t nextSegment{};
        return DotnetAnalysis::TryAcquireRetentionProfilerSegment(
            segments,
            1,
            &nextSegment,
            DotnetAnalysis::RetentionProfilerGraphRecordKind::Edge) == &header;
    }

    /// <summary>
    /// 确定性复现 claimant 观察旧 Writing 代后跨越发布、确认和复用才登记的交错；旧代 claimant 不得加入新代。
    /// </summary>
    bool StaleWriterClaimCannotEnterReusedGeneration()
    {
        DotnetAnalysis::RetentionProfilerSharedHeader header{};
        header.segmentState = static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Writing);
        header.edgeCapacity = 1;
        header.publicationSequence = 7;
        header.acknowledgedSequence = 7;
        const LONGLONG observedPublicationSequence = InterlockedCompareExchange64(
            &header.publicationSequence,
            0,
            0);
        if (InterlockedCompareExchange(
                &header.segmentState,
                static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Writing),
                static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Writing))
            != static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Writing))
        {
            return false;
        }

        LONGLONG nextPublicationSequence = 7;
        if (!DotnetAnalysis::TryPublishRetentionProfilerSegment(&header, &nextPublicationSequence))
        {
            return false;
        }

        const bool registrationClosed =
            !DotnetAnalysis::RetentionProfilerProtocolDetail::TryRegisterRetentionProfilerSegmentWriter(&header);
        if (!registrationClosed)
        {
            (void)DotnetAnalysis::RetentionProfilerProtocolDetail::ConfirmRetentionProfilerSegmentWriter(
                &header,
                8);
        }
        if (!DotnetAnalysis::TryAcknowledgeRetentionProfilerSegment(&header, 8))
        {
            return false;
        }

        DotnetAnalysis::RetentionProfilerSharedHeader* segments[]{ &header };
        std::uint32_t nextSegment{};
        if (DotnetAnalysis::TryAcquireRetentionProfilerSegment(
                segments,
                1,
                &nextSegment,
                DotnetAnalysis::RetentionProfilerGraphRecordKind::Edge)
            != &header
            || !DotnetAnalysis::RetentionProfilerProtocolDetail::TryRegisterRetentionProfilerSegmentWriter(&header))
        {
            return false;
        }

        const bool staleClaimAccepted =
            DotnetAnalysis::RetentionProfilerProtocolDetail::ConfirmRetentionProfilerSegmentWriter(
                &header,
                observedPublicationSequence);
        if (staleClaimAccepted)
        {
            DotnetAnalysis::CompleteRetentionProfilerSegmentWrite(&header);
        }
        return registrationClosed
            && !staleClaimAccepted
            && header.segmentState == static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Writing);
    }

    /// <summary>
    /// 已完成发布确认的非首代必须保持 writer 注册门关闭；开放门表示协议状态已损坏。
    /// </summary>
    bool AcknowledgedGenerationWithOpenWriterGateIsRejected()
    {
        DotnetAnalysis::RetentionProfilerSharedHeader header{};
        header.segmentState = static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Reusable);
        header.edgeCapacity = 1;
        header.publicationSequence = 6;
        header.acknowledgedSequence = 6;
        DotnetAnalysis::RetentionProfilerSharedHeader* segments[]{ &header };
        std::uint32_t nextSegment{};
        bool protocolCorrupted{};
        return DotnetAnalysis::TryAcquireRetentionProfilerSegment(
            segments,
            1,
            &nextSegment,
            DotnetAnalysis::RetentionProfilerGraphRecordKind::Edge,
            &protocolCorrupted) == nullptr
            && protocolCorrupted;
    }
}

/// <summary>
/// 验证 Controller 只接受版本匹配、容量有效且以空字符终止的附加协议。
/// </summary>
int main()
{
    static_assert(sizeof(DotnetAnalysis::RetentionProfilerSharedHeader) == 112);
    static_assert(sizeof(DotnetAnalysis::RetentionProfilerObjectRecord) == 24);
    static_assert(sizeof(DotnetAnalysis::RetentionProfilerTypeEvidenceRecord) == 1552);

    DotnetAnalysis::RetentionProfilerAttachData attachData{};
    attachData.version = DotnetAnalysis::kRetentionProfilerProtocolVersion;
    attachData.segmentCapacityBytes = 64 * 1024;
    attachData.segmentCount = 2;
    std::wmemcpy(attachData.segmentNames[0], L"DotnetAnalysis.Retention.Test.0", 31);
    std::wmemcpy(attachData.segmentNames[1], L"DotnetAnalysis.Retention.Test.1", 31);
    std::wmemcpy(attachData.completionEventName, L"DotnetAnalysis.Retention.Complete", 31);
    std::wmemcpy(attachData.failureEventName, L"DotnetAnalysis.Retention.Failed", 30);
    std::wmemcpy(attachData.detachEventName, L"DotnetAnalysis.Retention.Detached", 32);

    assert(DotnetAnalysis::IsValidRetentionProfilerAttachData(attachData));

    std::wmemset(
        attachData.segmentNames[2],
        L'X',
        sizeof(attachData.segmentNames[2]) / sizeof(attachData.segmentNames[2][0]));
    attachData.segmentCount = 3;
    assert(!DotnetAnalysis::IsValidRetentionProfilerAttachData(attachData));
    attachData.segmentNames[2][0] = L'\0';
    assert(DotnetAnalysis::IsValidRetentionProfilerAttachData(attachData));
    attachData.segmentCount = 2;

    attachData.segmentCapacityBytes = 0;
    assert(!DotnetAnalysis::IsValidRetentionProfilerAttachData(attachData));

    DotnetAnalysis::RetentionProfilerSharedHeader first{};
    DotnetAnalysis::RetentionProfilerSharedHeader second{};
    first.segmentState = static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Reusable);
    second.segmentState = static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Reusable);
    first.edgeCapacity = 2;
    second.edgeCapacity = 2;
    DotnetAnalysis::RetentionProfilerSharedHeader* segments[]{ &first, &second };
    std::uint32_t nextSegment{};
    LONGLONG nextPublicationSequence{};

    auto* acquired = DotnetAnalysis::TryAcquireRetentionProfilerSegment(
        segments,
        2,
        &nextSegment,
        DotnetAnalysis::RetentionProfilerGraphRecordKind::Edge);
    assert(acquired == &first);
    acquired->objectCount = 2;
    acquired->edgeCount = 2;
    assert(DotnetAnalysis::TryPublishRetentionProfilerSegment(acquired, &nextPublicationSequence));
    acquired = DotnetAnalysis::TryAcquireRetentionProfilerSegment(
        segments,
        2,
        &nextSegment,
        DotnetAnalysis::RetentionProfilerGraphRecordKind::Edge);
    assert(acquired == &second);
    acquired->objectCount = 2;
    acquired->edgeCount = 2;
    assert(DotnetAnalysis::TryPublishRetentionProfilerSegment(acquired, &nextPublicationSequence));

    assert(DotnetAnalysis::TryAcquireRetentionProfilerSegment(
        segments,
        2,
        &nextSegment,
        DotnetAnalysis::RetentionProfilerGraphRecordKind::Edge) == nullptr);
    assert(!DotnetAnalysis::TryAcknowledgeRetentionProfilerSegment(&first, 2));
    assert(DotnetAnalysis::TryAcquireRetentionProfilerSegment(
        segments,
        2,
        &nextSegment,
        DotnetAnalysis::RetentionProfilerGraphRecordKind::Edge) == nullptr);
    assert(DotnetAnalysis::TryAcknowledgeRetentionProfilerSegment(&first, 1));
    acquired = DotnetAnalysis::TryAcquireRetentionProfilerSegment(
        segments,
        2,
        &nextSegment,
        DotnetAnalysis::RetentionProfilerGraphRecordKind::Edge);
    assert(acquired == &first);
    assert(acquired->objectCount == 2);
    assert(acquired->edgeCount == 0);
    assert(acquired->publicationSequence == 1);
    assert(acquired->acknowledgedSequence == 1);

    DotnetAnalysis::RetentionProfilerSharedHeader ledgerFull{};
    ledgerFull.segmentState = static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Reusable);
    ledgerFull.objectCapacity = 1;
    ledgerFull.objectCount = 1;
    ledgerFull.edgeCapacity = 2;
    ledgerFull.edgeCount = 1;
    ledgerFull.publicationSequence = 7;
    ledgerFull.acknowledgedSequence = 7;
    ledgerFull.activeWriterCount = DotnetAnalysis::RetentionProfilerProtocolDetail::kWriterRegistrationClosed;
    DotnetAnalysis::RetentionProfilerSharedHeader unavailable{};
    unavailable.segmentState = static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Published);
    unavailable.publicationSequence = 9;
    DotnetAnalysis::RetentionProfilerSharedHeader* capacitySegments[]{ &ledgerFull, &unavailable };
    nextSegment = 0;
    nextPublicationSequence = 7;

    bool protocolCorrupted{};
    assert(DotnetAnalysis::TryAcquireRetentionProfilerSegment(
        capacitySegments,
        2,
        &nextSegment,
        static_cast<DotnetAnalysis::RetentionProfilerGraphRecordKind>(99),
        &protocolCorrupted) == nullptr);
    assert(protocolCorrupted);

    assert(DotnetAnalysis::TryAcquireRetentionProfilerSegment(
        capacitySegments,
        2,
        &nextSegment,
        DotnetAnalysis::RetentionProfilerGraphRecordKind::Object) == nullptr);
    assert(ledgerFull.segmentState == static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Reusable));
    assert(ledgerFull.edgeCount == 1);
    assert(!DotnetAnalysis::TryBeginRetentionProfilerSegmentWrite(&ledgerFull));

    ledgerFull.objectCount = 0;
    acquired = DotnetAnalysis::TryAcquireRetentionProfilerSegment(
        capacitySegments,
        2,
        &nextSegment,
        DotnetAnalysis::RetentionProfilerGraphRecordKind::Object);
    assert(acquired == &ledgerFull);
    assert(acquired->edgeCount == 0);
    assert(DotnetAnalysis::TryBeginRetentionProfilerSegmentWrite(acquired));
    acquired->objectCount = 1;
    acquired->edgeCount = 1;
    assert(DotnetAnalysis::TryPublishRetentionProfilerSegment(acquired, &nextPublicationSequence));
    assert(acquired->segmentState == static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Sealing));
    assert(!DotnetAnalysis::TryAcknowledgeRetentionProfilerSegment(acquired, 8));
    assert(DotnetAnalysis::TryAcquireRetentionProfilerSegment(
        capacitySegments,
        2,
        &nextSegment,
        DotnetAnalysis::RetentionProfilerGraphRecordKind::Edge) == nullptr);

    DotnetAnalysis::CompleteRetentionProfilerSegmentWrite(acquired);
    assert(acquired->segmentState == static_cast<LONG>(DotnetAnalysis::RetentionProfilerSegmentState::Published));
    assert(DotnetAnalysis::TryAcknowledgeRetentionProfilerSegment(acquired, 8));
    acquired = DotnetAnalysis::TryAcquireRetentionProfilerSegment(
        capacitySegments,
        2,
        &nextSegment,
        DotnetAnalysis::RetentionProfilerGraphRecordKind::Edge);
    assert(acquired == &ledgerFull);
    assert(acquired->edgeCount == 0);

    const bool failedClaimPublished = FailedWriterClaimPublishesSealedGeneration();
    const bool staleClaimRejected = StaleWriterClaimCannotEnterReusedGeneration();
    const bool openAcknowledgedGateRejected = AcknowledgedGenerationWithOpenWriterGateIsRejected();
    if (!failedClaimPublished)
    {
        std::fputs("FAILED: failed writer claim left the segment unpublished.\n", stderr);
    }
    if (!staleClaimRejected)
    {
        std::fputs("FAILED: stale writer claim crossed publication acknowledgement and reuse.\n", stderr);
    }
    if (!openAcknowledgedGateRejected)
    {
        std::fputs("FAILED: acknowledged generation exposed an open writer registration gate.\n", stderr);
    }
    return failedClaimPublished && staleClaimRejected && openAcknowledgedGateRejected ? 0 : 1;
}
