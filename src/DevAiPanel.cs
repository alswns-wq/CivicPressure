#if DEV
using ColossalFramework.UI;
using UnityEngine;

namespace CivicPressure
{
    // DEV-only foundation with a manually triggered, credential-free HTTPS probe.
    internal sealed class DevAiPanel : UIPanel
    {
        private UILabel context, status;
        private UITextField key, preview;
        private UIButton toggle, generate;
        private bool aiEnabled;
        private ComplaintView selected;
        private DevAiProbe probe;
        private UIButton connection;
        private UILabel connectionStatus;
        private DevAiProbe generation;
        private UITextField reply;
        private UIButton clear;
        private float nextRequest;
        private float nextRefresh;
        private bool gemini = true;
        private UIButton provider;
        private UILabel notice, model;

        internal void Initialize()
        {
            name = "CivicPressureAiTest";
            backgroundSprite = "MenuPanel2";
            size = new Vector2(920, 670);
            relativePosition = new Vector3(80, 120);
            Text("AI DEV · 0.6.9", 14, 12, 450, 26);
            UIDragHandle drag = AddUIComponent<UIDragHandle>();
            drag.size = new Vector2(870, 40); drag.target = this;
            Button("X", 878, 8, 28).eventClick += delegate { StopProbe(); Hide(); };
            toggle = Button("", 14, 50, 150);
            toggle.eventClick += delegate { aiEnabled = !aiEnabled; if (!aiEnabled && reply != null) reply.text = ""; Refresh(); };
            provider = Button("", 176, 50, 330);
            provider.eventClick += delegate {
                if (generation != null || probe != null || DevAiProbe.IsRunning) return;
                StopProbe(); gemini = !gemini; aiEnabled = false;
                key.text = ""; reply.text = ""; Refresh();
            };
            Text(L.T("API 키 · 도시를 나가면 삭제됩니다"), 14, 94, 490, 22);
            key = AddUIComponent<UITextField>();
            key.canFocus = true; key.isInteractive = true; key.builtinKeyNavigation = true;
            key.relativePosition = new Vector3(14, 120); key.size = new Vector2(400, 30);
            key.normalBgSprite = "TextFieldPanel"; key.textScale = .85f;
            key.isPasswordField = true; key.passwordCharacter = "*"; key.maxLength = 512;
            key.eventTextChanged += delegate { Refresh(); };
            clear = Button(L.T("지우기"), 420, 120, 86);
            clear.eventClick += delegate { key.text = ""; };
            context = Text("", 14, 168, 490, 64);
            notice = Text("", 14, 240, 490, 72);
            preview = AddUIComponent<UITextField>();
            preview.relativePosition = new Vector3(14, 318); preview.size = new Vector2(490, 120);
            preview.normalBgSprite = "TextFieldPanel"; preview.textScale = .75f;
            preview.multiline = true; preview.readOnly = true;
            generate = Button(L.T("답변 생성"), 14, 454, 180);
            generate.isEnabled = false;
            generate.eventClick += delegate { Generate(); };
            Text(L.T("AI 답변 초안 · 실제 조치는 적용되지 않습니다."), 534, 60, 370, 50);
            model = Text("", 534, 112, 370, 25);
            reply = AddUIComponent<UITextField>();
            reply.relativePosition = new Vector3(534, 150); reply.size = new Vector2(370, 410);
            reply.normalBgSprite = "TextFieldPanel"; reply.textScale = .85f;
            reply.multiline = true; reply.readOnly = true;
            status = Text("", 14, 498, 490, 64);
            connection = Button(L.T("키 없이 HTTPS 연결 확인"), 204, 454, 302);
            connectionStatus = Text(L.T("키·민원 데이터 전송 없이 연결만 검사합니다."), 14, 570, 490, 90);
            connection.eventClick += delegate {
                if (DevAiProbe.IsRunning || generation != null || (probe != null && probe.Result == null)) return;
                probe = new DevAiProbe(gemini);
                DevAiProbe active = probe;
                connection.isEnabled = false;
                connectionStatus.text = L.T("HTTPS 연결 확인 중… 잠시 기다려 주세요.");
                System.Threading.ThreadPool.QueueUserWorkItem(delegate { active.Run(); });
            };
            Refresh();
        }

        public override void Update()
        {
            base.Update();
            if (generation != null && generation.Result != null)
            {
                string answer = generation.Result;
                reply.text = answer.StartsWith("OK\n") ? answer.Substring(3) : ErrorText(answer);
                generation = null;
            }
            if (Time.realtimeSinceStartup >= nextRefresh) { nextRefresh = Time.realtimeSinceStartup + .25f; Refresh(); }
            if (probe == null || probe.Result == null) return;
            string result = probe.Result;
            connectionStatus.text = (probe.HttpsReached ? L.T("HTTPS 응답 확인 · 키 없는 요청의 정상 거절: ") :
                L.T("연결 검사 결과 · 인증/생성은 미검증: ")) + result;
            connectionStatus.tooltip = probe.Diagnostic ?? result;
            Debug.Log("[CivicPressure] AI HTTPS probe: " + (probe.Diagnostic ?? result));
            connection.isEnabled = true; probe = null;
        }

        private void StopProbe()
        {
            if (generation != null) { generation.Cancel(); if (reply != null) reply.text = ""; }
            generation = null;
            if (probe != null) probe.Cancel();
            probe = null;
            if (connection != null) connection.isEnabled = true;
            if (connectionStatus != null) connectionStatus.text = L.T("키·민원 데이터 전송 없이 연결만 검사합니다.");
        }

        public override void OnDisable() { StopProbe(); base.OnDisable(); }

        internal void Select(ComplaintView complaint)
        {
            if (selected == null || complaint == null || selected.Key != complaint.Key || selected.Region != complaint.Region || selected.Stamp != complaint.Stamp)
            {
                if (generation != null) generation.Cancel();
                generation = null;
                if (reply != null) reply.text = "";
            }
            selected = complaint;
            if (context != null) Refresh();
        }

        private void Refresh()
        {
            if (status == null) return;
            toggle.text = aiEnabled ? L.T("AI 시험 켜짐") : L.T("AI 시험 꺼짐");
            provider.text = gemini ? "Google Gemini" : "OpenAI";
            provider.tooltip = L.T("클릭하면 제공자를 변경합니다. 입력한 키는 지워집니다.");
            model.text = gemini ? "Google · " + DevAiReply.GeminiModel : "OpenAI · " + DevAiReply.Model;
            notice.text = gemini ? L.T("생성 시 아래 정보를 Google로 전송합니다. 무료 티어는 한도가 있으며 입력·출력이 제품 개선에 쓰일 수 있습니다. 유료 프로젝트는 요금이 발생할 수 있습니다.") :
                L.T("생성 시 아래 정보를 OpenAI로 전송합니다. API 이용료가 발생할 수 있습니다.");
            // This payload is explicitly bounded to the selected service, never the whole city.
            bool supported = selected != null && selected.AiService != null;
            string value = !supported ? L.T("일반 서비스 민원을 선택하세요.") :
                selected.AiDistrict + " · " + selected.AiService + "\n" +
                L.T("해당 서비스 피해 / 심각 / 집계 대상: ") + selected.AiAffected + " / " + selected.AiSevere + " / " + selected.AiPopulation;
            if (context.text != value) context.text = value;
            string input = !supported ? "" : DevAiInput.Encode(selected.AiDistrict, selected.AiService,
                selected.AiAffected, selected.AiSevere, selected.AiPopulation);
            if (preview.text != input) preview.text = input;
            bool busy = generation != null || probe != null || DevAiProbe.IsRunning;
            provider.isEnabled = !busy;
            toggle.isEnabled = !busy; key.readOnly = busy; clear.isEnabled = !busy;
            connection.isEnabled = !busy && probe == null;
            generate.isEnabled = aiEnabled && supported && DevAiReply.ValidKey(key.text.Trim()) && !busy && Time.realtimeSinceStartup >= nextRequest;
            status.text = !aiEnabled ? L.T("AI가 꺼져 있습니다. 외부로 전송하지 않습니다.") :
                !supported ? L.T("일반 서비스 민원을 선택하세요.") :
                busy ? L.T("요청 처리 중… 자동 재시도하지 않습니다.") :
                !DevAiReply.ValidKey(key.text.Trim()) ? L.T("API 키를 입력하세요.") :
                L.T("답변 생성을 누르면 한 번 요청합니다. 초안은 게임에 자동 적용되지 않습니다.");
        }

        private void Generate()
        {
            Refresh(); if (!generate.isEnabled) return;
            StopProbe();
            generation = new DevAiProbe(gemini); DevAiProbe active = generation;
            string credential = key.text.Trim();
            string body = gemini ? DevAiReply.GeminiRequest(preview.text, L.Code(L.Current)) : DevAiReply.Request(preview.text, L.Code(L.Current));
            reply.text = L.T("요청 처리 중… 자동 재시도하지 않습니다.");
            nextRequest = Time.realtimeSinceStartup + 5f;
            Refresh();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { try { active.Generate(credential, body); } finally { credential = null; body = null; } });
        }

        private string ErrorText(string code)
        {
            string primary = code.Split('\n')[0];
            return ErrorSummary(primary) + "\n\n" + code;
        }

        private string ErrorSummary(string code)
        {
            if (code == "HTTP 401" || code == "InvalidKey") return L.T("API 키 인증 실패. 키를 확인하세요.");
            if (code == "Quota") return L.T("API 잔액 또는 사용 한도가 부족합니다. OpenAI 결제 설정을 확인하세요.");
            if (code == "HTTP 429") return gemini ? L.T("Gemini 요청·토큰 또는 무료 사용 한도에 도달했습니다. AI Studio에서 한도를 확인하세요. 자동 재시도하지 않습니다.") :
                L.T("요청 또는 계정 한도에 도달했습니다. API 잔액과 사용 한도를 확인하세요.");
            if (code == "Refused") return L.T("AI가 답변 생성을 거절했습니다. 다른 민원으로 시험하세요.");
            if (code == "Incomplete") return L.T("답변이 길이 제한으로 중단됐습니다. 완성된 답변은 없습니다.");
            if (code == "HTTP 403") return L.T("접근이 거부됐습니다. 키 제한과 프로젝트 API 권한을 확인하세요.");
            if (code == "HTTP 404") return L.T("요청한 모델 또는 API 경로를 찾지 못했습니다. 아래 오류 코드를 알려주세요.");
            return L.T("답변 생성 실패. 자동 재시도하지 않았습니다: ") + code;
        }

        private UILabel Text(string value, float x, float y, float w, float h)
        {
            UILabel label = AddUIComponent<UILabel>();
            label.autoSize = false; label.autoHeight = false; label.wordWrap = true;
            label.relativePosition = new Vector3(x, y); label.size = new Vector2(w, h);
            label.textScale = .85f; label.text = value; return label;
        }

        private UIButton Button(string value, float x, float y, float w)
        {
            UIButton button = AddUIComponent<UIButton>();
            button.relativePosition = new Vector3(x, y); button.size = new Vector2(w, 30);
            button.text = value; button.textScale = .8f;
            button.normalBgSprite = "ButtonMenu"; button.hoveredBgSprite = "ButtonMenuHovered";
            button.disabledBgSprite = "ButtonMenuDisabled";
            button.pressedBgSprite = "ButtonMenuPressed"; return button;
        }

        public override void OnDestroy()
        {
            StopProbe();
            if (key != null) key.text = "";
            selected = null;
            base.OnDestroy();
        }
    }
}
#endif
