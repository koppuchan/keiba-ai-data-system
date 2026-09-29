using System;

namespace KeibaDataCollector.Models
{
    /// <summary>仕様書§5「JRA取得」「地方データ契約」列に対応。データそのものを取得してよいか。</summary>
    public enum AcquisitionState
    {
        Inactive = 0,
        Active = 1,
    }

    /// <summary>仕様書§5「JRA Web公開許諾」「JRA商用利用許諾」「地方Web公開許諾」列に対応。
    /// 取得できることとWebに公開してよいことは別問題、という仕様書の大前提を型で表現する。</summary>
    public enum ApprovalState
    {
        Pending = 0,
        Approved = 1,
        Rejected = 2,
    }

    /// <summary>JRA-VAN側は開催場ごとの契約ではなく全国一律のため、venue単位ではなく
    /// システム全体で1件のみ存在する設定として扱う。</summary>
    public class JraLicenseState
    {
        public AcquisitionState AcquisitionState { get; set; } = AcquisitionState.Inactive;
        public ApprovalState WebPublishApproval { get; set; } = ApprovalState.Pending;
        public ApprovalState CommercialUseApproval { get; set; } = ApprovalState.Pending;
        public string Note { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    /// <summary>地方競馬DATA(UmaConn)は競馬場（venue）単位で契約が分かれるため、
    /// venue_idごとに行を持つ。行が存在しない venue は「契約対象外」として扱う
    /// （仕様書§5「地方対象場：契約対象のみ」）。</summary>
    public class LocalVenueLicenseState
    {
        public string VenueId { get; set; }
        public string VenueName { get; set; }
        public AcquisitionState ContractState { get; set; } = AcquisitionState.Inactive;
        public ApprovalState WebPublishApproval { get; set; } = ApprovalState.Pending;
        public string Note { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
