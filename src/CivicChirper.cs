using System;
using ColossalFramework;
using UnityEngine;

namespace CivicPressure
{
    internal static class CivicChirper
    {
        internal const string SenderPrefix = "CivicPressure.OfficeSender.v1:";
        internal const string TargetPrefix="CivicPressure.OfficeSender.v2:";
        internal static string TargetKey(ushort building,uint index,string sender){return TargetPrefix+building+":"+index+":"+sender;}
        internal static bool Target(CitizenMessage message,out ushort building,out uint index,out string sender)
        {
            building=0;index=0;sender=null;
            if(!(message is CustomCitizenMessage) || message.m_keyID==null || !message.m_keyID.StartsWith(TargetPrefix,StringComparison.Ordinal))return false;
            string[] parts=message.m_keyID.Substring(TargetPrefix.Length).Split(new char[]{':'},3);
            return parts.Length==3 && ushort.TryParse(parts[0],out building) && uint.TryParse(parts[1],out index) && (sender=parts[2]).Length>0;
        }
        internal static string CitySender(CityView city)
        {
            return city.HasCityHall && !string.IsNullOrEmpty(city.CityHall) ? city.CityHall : AdministrationTitle.City(city.CityName);
        }
        internal static string ComplaintSender(CityView city, int region)
        {
            RegionView district = region > 0 && region < city.Regions.Length ? city.Regions[region] : null;
            string office = district != null && district.OfficeTransferred && !string.IsNullOrEmpty(district.ServiceOffice) ? district.ServiceOffice :
                district != null && district.OfficeId != 0 && !string.IsNullOrEmpty(district.Office) ? district.Office : CitySender(city);
            return office + L.T(" 민원접수처");
        }
        internal static string TransferText(RegionView region)
        {
            string notice = region.Name + L.T(" 구청이 재난으로 운영을 중단했습니다. ");
            return region.ServiceOfficeId != 0
                ? notice + L.T("민원 업무를 담당할 행정청은 ") + region.ServiceOffice + L.T("입니다. ") + L.T("구청 청사를 신속히 복구하거나 다시 지정해야 합니다. #행정안내")
                : notice + L.T("대체 행정청이 없어 민원 접수가 일시 중단되었습니다. ") + L.T("구청 청사를 신속히 복구하거나 다시 지정해야 합니다. #행정안내");
        }
        internal static string CityHallClosureText(CityView city)
        {
            return city.CityHall + L.T(" 청사가 재난으로 운영을 중단했습니다. 시청 청사를 신속히 복구하거나 다시 지정해야 합니다. #행정안내");
        }
        internal static bool OfficialSender(CitizenMessage message, out string sender)
        {
            ushort building;uint index;
            if(Target(message,out building,out index,out sender))return true;
            sender = null;
            if (!(message is CustomCitizenMessage) || message.m_keyID == null || !message.m_keyID.StartsWith(SenderPrefix, StringComparison.Ordinal)) return false;
            sender = message.m_keyID.Substring(SenderPrefix.Length);
            return sender.Length > 0;
        }
        internal static string Text(string kind, string area)
        {
            switch (kind)
            {
                // Posted under an administrative office's name, so these read as public notices, not citizen chatter.
                case "protest": return area + L.T(" 주민 집회 행렬이 시청 방면으로 이동 중입니다. 해당 구간 통행에 협조하여 주시기 바랍니다. #집회안내 #교통협조");
                case "riot": return area + L.T("에서 폭동이 발생했습니다. 인근 주민과 통행 차량은 현장 접근을 삼가시기 바랍니다. #폭동 #안전안내");
                case "escalation": return area + L.T(" 집회가 폭동으로 확산되었습니다. 현장 일대 통행을 통제하고 있으며, 인근 주민은 외출을 자제하여 주시기 바랍니다. #폭동 #긴급상황");
                case "sit-in": return area + L.T(" 집회 인원이 시청 앞에 도착해 농성에 들어갔습니다. 시청 민원 업무와 주변 통행에 지연이 예상됩니다. #시청 #농성");
                case "damage": return area + L.T(" 폭동 현장에서 건물 화재 및 붕괴 피해가 확인되었습니다. 인근 접근을 자제하여 주시기 바랍니다. #폭동피해 #안전안내");
                case "police": return area + L.T(" 폭동 현장에 경찰이 도착해 대치 중입니다. 상황이 정리될 때까지 우회 통행하여 주시기 바랍니다. #경찰출동 #우회안내");
                case "riot-end": return area + L.T(" 폭동 상황이 종료되었습니다. 경찰 진압으로 군중이 해산했으며 현장 통제를 해제했습니다. #상황종료 #폭동해산");
                case "protest-end": return area + L.T(" 집회가 자진 해산으로 종료되었습니다. 제기된 요구 사항은 계속 검토하겠습니다. #상황종료 #집회해산");
                default: throw new ArgumentException("Unknown civic news event", "kind");
            }
        }

        // Called only at committed simulation transitions; native messages retain native save compatibility.
        internal static void Post(UnrestEvent e, string kind)
        {
            try
            {
                string area = e.Origin < 0 ? L.T("우리 도시") : e.Origin == 0 ? L.T("지구 밖 지역") : Singleton<DistrictManager>.instance.GetDistrictName((byte)e.Origin);
                PostText(Text(kind, string.IsNullOrEmpty(area) ? L.T("우리 지역") : area));
            }
            catch (Exception error) { Debug.LogWarning("[CivicPressure] Chirper notification failed: " + error.Message); }
        }

        internal static void PostText(string text)
        {
            CityView city=CivicSimulation.View; PostText(text,CitySender(city),city.CityHallId);
        }
        internal static void PostComplaint(string text,CityView city,int region)
        {
            RegionView area=region>0 && region<city.Regions.Length ? city.Regions[region] : null;
            PostText(text,ComplaintSender(city,region),area!=null && area.OfficeTransferred ? area.ServiceOfficeId : area!=null && area.OfficeId!=0 ? area.OfficeId : city.CityHallId);
        }
        internal static void PostText(string text,string sender){PostText(text,sender,(ushort)0);}
        private static void PostText(string text, string sender,ushort building)
        {
            try
            {
                MessageManager manager = Singleton<MessageManager>.instance;
                // Native CustomCitizenMessage ignores keyID when rendering text but persists it.
                // Sender ID zero avoids linking an administrative notice to an unrelated citizen.
                CustomCitizenMessage notice = new CustomCitizenMessage(0, text, null);
                Building[] buildings=Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                notice.m_keyID = building!=0 && building<buildings.Length && CivicSimulation.Live(buildings[building])
                    ? TargetKey(building,buildings[building].m_buildIndex,sender) : SenderPrefix + sender;
                manager.QueueMessage(notice);
            }
            catch (Exception error)
            {
                // Notification failures must not stop the event simulation or leave traffic locked.
                Debug.LogWarning("[CivicPressure] Chirper notification failed: " + error.Message);
            }
        }

    }
}
