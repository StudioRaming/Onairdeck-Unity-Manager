// Copy of MingPainter-dev Assets/StudioRaming/MingPainter/Editor/UI/Theme/StudioRamingEditorTheme.cs (itself derived from
// MaskMaker 2692ad0). Changes here: namespace renamed to OnAirDeck.UnityManager.Theme, and SRProduct.OnAirDeck added
// (the Manager's accent = the family Mint, the OnAirDeck web primary). Nothing else is edited; regenerate the palette
// block from tokens.json, do not hand-edit colours.
// ------------------------------------------------------------------------------------
// StudioRaming Editor Theme: reference implementation for Unity IMGUI (C# 7.3, Unity 2021.3+).
//
// Source of truth: the "StudioRaming Editor Theme" design system (tokens.json). The palette
// block below is GENERATED from those tokens; do not hand-edit colors, regenerate instead.
//
// Adoption:
//  - Copy this file into the product's Editor folder and rename the namespace to
//    StudioRaming.<Product>.UI so two products in one project never collide.
//  - Every color painted on screen comes from SRTheme.P. No new Color(...) in window code.
//  - Unity metrics stay: 18px line, 2px spacing, 20/24 buttons, 15px indent, label width
//    max(width * 0.45 - 40, 120), 275px minimum inspector width.
//  - Unity behaviors stay: label drag on numeric fields, prefab override bold + context menu
//    (SerializedProperty overloads), Undo through SerializedObject, keyboard focus.
//  - Unity-drawn fields (ObjectField, CurveField, ColorField, tooltips, dialogs, progress
//    dialogs) are not redrawn. CurveField gets the brand ink.
//  - Hover states need EditorWindow.wantsMouseMove = true.
//  - Disabled text (GUI.enabled false, EditorGUI.DisabledScope) is text-disabled, applied once.
//    Unity blends every GUIStyle text at half alpha while disabled but leaves DrawTexture faces
//    alone, so SRGui draws its own text with that blend suspended (SRGui.Ink) and gives text
//    Unity draws itself a pre-compensated colour (SRTheme.DisabledInk). Window code that paints
//    its own disabled text uses the same two calls.
// ------------------------------------------------------------------------------------
using System;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager.Theme
{
    public enum SRTone { Neutral, Brand, Success, Warning, Danger, Info }

    public enum SRProduct { MingToon, Physics, CharacterToolbox, MaskMaker, EasyMeshEditor, WarudoBatchBuilder, OnAirDeck }

    public enum SRButtonKind { Primary, Secondary, Ghost, Danger }

    public enum SRButtonSize { Mini, Default, Large, Hero }

    /// <summary>Glyphs drawn without the editor font (SRGui.Glyph).</summary>
    public enum SRGlyph { Check, ChevronDown, ChevronRight, Close, Search }

    /// <summary>Stored as int in EditorPrefs (0 = ko, 1 = en, 2 = ja), shared with existing tools.</summary>
    public enum SRLang { Korean = 0, English = 1, Japanese = 2 }

    /// <summary>
    /// Product-family language setting. Contract shared with TextureColor, CharacterToolbox and
    /// MingToon: the shared int key StudioRaming.Language wins; when it is missing, the product's
    /// own legacy key is migrated once; writes go to both keys; legacy keys are never deleted.
    /// Call Invalidate() from EditorWindow.OnFocus so a change made in another tool is picked up.
    /// </summary>
    public static class SRLanguage
    {
        public const string SharedKey = "StudioRaming.Language";
        private static int cached = -1;
        private static int version;

        /// <summary>Bumped on every change; use it to invalidate cached GUIContent.</summary>
        public static int Version { get { return version; } }

        public static SRLang Get(string productKey)
        {
            if (cached < 0)
            {
                int fallback = Application.systemLanguage == SystemLanguage.Korean ? 0
                    : Application.systemLanguage == SystemLanguage.Japanese ? 2 : 1;
                if (EditorPrefs.HasKey(SharedKey))
                {
                    cached = EditorPrefs.GetInt(SharedKey, fallback);
                }
                else
                {
                    cached = string.IsNullOrEmpty(productKey) ? fallback : EditorPrefs.GetInt(productKey, fallback);
                    EditorPrefs.SetInt(SharedKey, cached);
                }
            }
            return (SRLang)Mathf.Clamp(cached, 0, 2);
        }

        /// <summary>Raised after Set. Windows can subscribe in OnEnable (unsubscribe in OnDisable).</summary>
        public static event Action Changed;

        private static readonly System.Collections.Generic.List<Type> windowTypes = new System.Collections.Generic.List<Type>();

        /// <summary>Also repaint windows of this type on Set (for windows outside a StudioRaming namespace).</summary>
        public static void RegisterWindowType(Type windowType)
        {
            if (windowType != null && !windowTypes.Contains(windowType)) windowTypes.Add(windowType);
        }

        public static void Set(SRLang value, string productKey)
        {
            cached = (int)value;
            version++;
            EditorPrefs.SetInt(SharedKey, (int)value);
            if (!string.IsNullOrEmpty(productKey)) EditorPrefs.SetInt(productKey, (int)value);
            RepaintStudioRamingWindows();
            if (Changed != null) Changed();
        }

        /// <summary>Forget the cached value (another tool may have changed it).</summary>
        public static void Invalidate()
        {
            if (cached < 0) return;
            int before = cached;
            cached = -1;
            if (EditorPrefs.HasKey(SharedKey) && EditorPrefs.GetInt(SharedKey, before) != before) version++;
        }

        /// <summary>Pick the string for the current language.</summary>
        public static string Pick(string productKey, string ko, string en, string ja)
        {
            switch (Get(productKey))
            {
                case SRLang.Korean: return ko;
                case SRLang.Japanese: return ja;
                default: return en;
            }
        }

        private static void RepaintStudioRamingWindows()
        {
            foreach (EditorWindow w in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                Type type = w.GetType();
                string ns = type.Namespace;
                bool ours = ns != null && ns.StartsWith("StudioRaming", StringComparison.Ordinal);
                for (int i = 0; !ours && i < windowTypes.Count; i++) ours = windowTypes[i].IsAssignableFrom(type);
                if (ours) w.Repaint();
            }
        }
    }

    /// <summary>One status tone: Mark = icon/dot, Text = readable text, Bg/Border = box face and edge.</summary>
    public struct SRToneColors
    {
        public Color Mark;
        public Color Text;
        public Color Bg;
        public Color Border;

        public SRToneColors(Color mark, Color text, Color bg, Color border)
        {
            Mark = mark;
            Text = text;
            Bg = bg;
            Border = border;
        }
    }

    public sealed class SRPalette
    {
        /// <summary>창 헤더 띠, 앱 바. 제목은 text-strong.</summary>
        public Color SurfaceApp;
        /// <summary>창·인스펙터 전체 배경. 모든 창은 이 색으로 먼저 칠한다.</summary>
        public Color SurfaceWindow;
        /// <summary>카드, 폴드아웃 본문, 목록 영역.</summary>
        public Color SurfaceCard;
        /// <summary>섹션(폴드아웃) 머리 줄, 탭 줄 바닥, 표 머리.</summary>
        public Color SurfaceSection;
        /// <summary>목록 행·카드에 마우스를 올렸을 때.</summary>
        public Color SurfaceHover;
        /// <summary>새로 그린 입력칸(숫자·텍스트·검색) 안쪽. Unity 입력칸처럼 창보다 들어가 보이게.</summary>
        public Color SurfaceSunken;
        /// <summary>모듈(탭) 머리 줄 띠. 펼쳐져 있어도 머리로 읽히게 한다.</summary>
        public Color HeaderBand;
        /// <summary>모듈 머리 줄에 마우스를 올렸을 때.</summary>
        public Color HeaderBandHover;
        /// <summary>모듈 머리 줄 아래 1px 구분선(머리와 자기 본문의 경계).</summary>
        public Color HeaderRule;
        /// <summary>본문 행 지브라 띠. 한 줄 걸러 칠해 행 구분을 만든다.</summary>
        public Color RowBand;
        /// <summary>보조 버튼·드롭다운·토글 버튼 면. 글자는 text-strong(Unity 버튼 글자처럼 라벨보다 밝게).</summary>
        public Color Control;
        /// <summary>보조 버튼 호버.</summary>
        public Color ControlHover;
        /// <summary>보조 버튼 누름, 켜진 토글 버튼 면.</summary>
        public Color ControlPressed;
        /// <summary>구분선, 카드 테두리, 목록 행 사이.</summary>
        public Color LineSubtle;
        /// <summary>입력칸·버튼 테두리. Unity 컨트롤 테두리와 같은 세기.</summary>
        public Color LineControl;
        /// <summary>꺼진 체크박스, 남은 단계 원처럼 테두리만으로 모양을 알아봐야 하는 표시. window·card·section 위 3:1 이상.</summary>
        public Color LineStrong;
        /// <summary>창 제목, 섹션 제목, 강조 값. surface-app·window·card·section·brand-tint 위.</summary>
        public Color TextStrong;
        /// <summary>본문과 라벨 기본. window·card·section·hover·sunken·control 위.</summary>
        public Color Text;
        /// <summary>부제, 단위, 원어 괄호, 힌트. window·card·section·sunken 위.</summary>
        public Color TextMuted;
        /// <summary>비활성 라벨·값·버튼 글자. WCAG 예외지만 Unity 비활성(약 2.5~3:1)처럼 읽히게 2.6:1 이상을 지킨다(v3.3, MM 캡처에서 1.6:1로 빈 화면의 주 버튼이 사라져 보였음). 이유는 툴팁으로 설명.</summary>
        public Color TextDisabled;
        /// <summary>주 버튼 면, 켜진 체크박스, 슬라이더 채움, 선택 탭 밑줄. 원본: mingtoon-site --pink.</summary>
        public Color Brand;
        /// <summary>주 버튼 호버.</summary>
        public Color BrandHover;
        /// <summary>주 버튼 누름.</summary>
        public Color BrandPressed;
        /// <summary>주 버튼·켜진 체크박스 테두리(라이트에서 배경과 분리).</summary>
        public Color BrandBorder;
        /// <summary>얇은 브랜드 표시: 선택 탭·세그먼트 밑줄, 현재 단계 테두리, 슬라이더 채움, 선택 행 테두리, 받음 테두리, 켜진 토글 버튼 테두리. 라이트의 brand는 면용으로 옅어서 선으로는 3:1이 안 되므로 따로 둔다(MM 세션 검토 2026-09-24).</summary>
        public Color BrandLine;
        /// <summary>brand·brand-hover·brand-pressed 면 위 글자와 체크 표시.</summary>
        public Color TextOnBrand;
        /// <summary>분홍 글자·아이콘(강조 숫자, 링크형 동작). window·card·section 위. 선택 표시 면(brand-tint) 위 글자는 text-strong.</summary>
        public Color BrandText;
        /// <summary>보조 브랜드색. 포커스 테두리, 링크, 진행 중 표시. 원본: mingtoon-site --mint.</summary>
        public Color Mint;
        /// <summary>키보드 포커스 1px 테두리(Unity 규칙의 파란 테두리 자리). 모든 면에서 3:1 이상.</summary>
        public Color Focus;
        /// <summary>문서·웹 링크 글자. 라이트는 글자 대비를 위해 민트를 진하게.</summary>
        public Color Link;
        /// <summary>선택된 목록 행, 선택된 탭·세그먼트 면. 글자는 text-strong.</summary>
        public Color BrandTint;
        /// <summary>완료·정상. 아이콘·점·진행 표시. 글자에는 success-text.</summary>
        public Color Success;
        /// <summary>success 톤 글자. success-bg·window·card 위.</summary>
        public Color SuccessText;
        /// <summary>success 도움말 상자·배지 면.</summary>
        public Color SuccessBg;
        /// <summary>success 도움말 상자·배지 테두리.</summary>
        public Color SuccessBorder;
        /// <summary>진행은 되지만 결과가 의도와 다를 수 있음. 다크 표시색은 Unity 도움말 상자 경고색. 아이콘·점·진행 표시. 글자에는 warning-text.</summary>
        public Color Warning;
        /// <summary>warning 톤 글자. warning-bg·window·card 위.</summary>
        public Color WarningText;
        /// <summary>warning 도움말 상자·배지 면.</summary>
        public Color WarningBg;
        /// <summary>warning 도움말 상자·배지 테두리.</summary>
        public Color WarningBorder;
        /// <summary>작업이 끝나지 않음, 지우기 같은 되돌리기 어려운 동작. 표시색은 Unity 도움말 상자 오류색. 아이콘·점·진행 표시. 글자에는 danger-text.</summary>
        public Color Danger;
        /// <summary>danger 톤 글자. danger-bg·window·card 위.</summary>
        public Color DangerText;
        /// <summary>danger 도움말 상자·배지 면.</summary>
        public Color DangerBg;
        /// <summary>danger 도움말 상자·배지 테두리.</summary>
        public Color DangerBorder;
        /// <summary>참고 정보. 아이콘·점·진행 표시. 글자에는 info-text.</summary>
        public Color Info;
        /// <summary>info 톤 글자. info-bg·window·card 위.</summary>
        public Color InfoText;
        /// <summary>info 도움말 상자·배지 면.</summary>
        public Color InfoBg;
        /// <summary>info 도움말 상자·배지 테두리.</summary>
        public Color InfoBorder;
        /// <summary>MingToon 제품 표시(헤더 마크·헤더 밑줄).</summary>
        public Color ProductMingtoon;
        /// <summary>물리 엔진 제품 표시.</summary>
        public Color ProductPhysics;
        /// <summary>CharacterToolbox 제품 표시. 다크 값은 기존 CTB 액센트.</summary>
        public Color ProductCtb;
        /// <summary>MaskMaker 제품 표시. 다크 값은 기존 MM 액센트.</summary>
        public Color ProductMm;
        /// <summary>EasyMeshEditor 제품 표시. 라밍 결정(2026-09-24): 블렌더 같은 주황. MM 초록과 분리.</summary>
        public Color ProductEme;
        /// <summary>Warudo Batch Builder 제품 표시. 기존 WBB 라벤더·인디고.</summary>
        public Color ProductWbb;
        /// <summary>씬 뷰 추가 호버(기존 도구 값 #33E5FF 35%).</summary>
        public Color SceneAdd;
        /// <summary>씬 뷰 제거 호버(기존 도구 값 #FF4033 35%).</summary>
        public Color SceneRemove;
        /// <summary>씬 뷰 안내 글 배경(기존 값 검정 55%). 글자는 scene-hint-text.</summary>
        public Color SceneHintBg;
        /// <summary>씬 뷰 안내 글.</summary>
        public Color SceneHintText;
        /// <summary>작업 중 창을 덮는 막. 원본 잉크색.</summary>
        public Color Scrim;

        // ------------------------------------------------------------- inspector chrome
        // Values the pinned material header (MingPinnedMaterialHeader*), the search popup
        // and the texture module shipped with. They were inline `new Color(...)` at the
        // call sites before the 2026-09-30 tokenization pass; the values are unchanged.

        /// <summary>핀 헤더 띠. 전체 툴바를 그리지 않을 때의 대체 배경(NavStrip).</summary>
        public Color PinnedNavFallbackBand;
        /// <summary>전체 툴바 배경(모드가 Bulk일 때).</summary>
        public Color PinnedToolbarBand;
        /// <summary>핀 툴바에서 켜진 세그먼트 면.</summary>
        public Color PinnedNavSelected;
        /// <summary>nav 버튼 액센트가 밝을 때 쓰는 어두운 캡션 잉크(휘도 판정).</summary>
        public Color NavCaptionOnLightFill;
        /// <summary>nav 버튼 액센트가 어두울 때 쓰는 밝은 캡션 잉크(휘도 판정).</summary>
        public Color NavCaptionOnDarkFill;
        /// <summary>비용 게이지 홈.</summary>
        public Color CostGaugeTrack;
        /// <summary>검색 칸의 안내 문구(두 스킨 공통 회색).</summary>
        public Color SearchPlaceholder;
        /// <summary>검색 팝업의 선택 행 강조.</summary>
        public Color SearchSelection;
        /// <summary>게이트 행(스위치가 아래 행을 소유하는 행)의 액센트 레일.</summary>
        public Color GateRowAccent;
        /// <summary>텍스처 갤러리에서 지금 고른 칸의 강조.</summary>
        public Color GalleryCellSelected;
        /// <summary>텍스처 갤러리에서 적용된 칸의 강조.</summary>
        public Color GalleryCellApplied;
        /// <summary>베이크 후 편집 유지 행의 옅은 배경.</summary>
        public Color BakeKeepTintFace;
        /// <summary>베이크 후 편집 유지 행의 테두리.</summary>
        public Color BakeKeepTintEdge;

        // Material cost panel (Performance/MingMaterialCostPanel): the gauge and
        // percent colour by load band (under 40 / 60 / 80 %, and above). Same
        // value on both skins, as the panel shipped.
        /// <summary>비용 40% 미만.</summary>
        public Color CostGradeLight;
        /// <summary>비용 40~60%.</summary>
        public Color CostGradeModerate;
        /// <summary>비용 60~80%.</summary>
        public Color CostGradeHeavy;
        /// <summary>비용 80% 이상.</summary>
        public Color CostGradeHeaviest;
    }

    /// <summary>Unity editor metrics shared by every StudioRaming window.</summary>
    public static class SRMetrics
    {
        public const float Line = 18f;
        public const float Spacing = 2f;
        public const float Row = 20f;
        public const float ListRow = 22f;
        public const float ButtonMini = 18f;
        public const float Button = 20f;
        public const float ButtonLarge = 24f;
        public const float ButtonHero = 32f;
        public const float Tab = 24f;
        public const float Header = 44f;
        public const float AccentLine = 2f;
        public const float Indent = 15f;
        public const float LabelMin = 120f;
        public const float InspectorMin = 275f;
        public const float RadiusControl = 3f;
        public const float RadiusButton = 4f;
        public const float RadiusCard = 6f;
        public const float RadiusPill = 9f;
        public const float Space4 = 4f;
        public const float Space6 = 6f;
        public const float Space8 = 8f;
        public const float Space12 = 12f;
        public const float Space16 = 16f;

        // ---------------------------------------------------------- inspector
        // The Full/Simple inspectors' own layout values, grouped by what they
        // draw, so a category can be retuned from this file alone (owner,
        // 2026-09-29: "하드코딩말고, 다 재사용가능한 토큰으로 써").

        /// <summary>모듈(탭) 머리 줄 높이. 목록 행보다 한 단계 넉넉하다.</summary>
        public const float ModuleRowHeight = ListRow + Space6;
        /// <summary>모듈 머리 줄의 좌·우 안쪽 여백.</summary>
        public const float ModuleRowLeftInset = Space8;
        public const float ModuleRowRightInset = Space6;
        /// <summary>머리 줄과 자기 본문을 가르는 구분선 두께.</summary>
        public const float Hairline = 1f;
        /// <summary>모듈 본문(행 묶음)의 위·아래·좌우 여백.</summary>
        public const float SectionBodyPaddingTop = Space4;
        public const float SectionBodyPaddingBottom = Space8;
        public const float SectionBodySidePadding = 5f;
        public const float SectionBodySideMargin = 1f;
        /// <summary>본문이 끝나고 다음 모듈 머리가 시작되기까지의 빈 줄.</summary>
        public const float SectionBottomGap = Space8;
        /// <summary>섹션 안 카드가 좌우로 비우는 값(모듈 본문과 같은 리듬).</summary>
        public const float BodyRowIndent = Indent;

        // Row anatomy (MingInspectorRowControls): one token per role, so the
        // switch/label/field mosaic of a material row retunes here. Values are
        // the numbers the row file shipped with - this is a move, not a change
        // (owner, 2026-09-30: "토큰화 제대로 안되어있는것들 전부 찾아서 토큰화").
        public const float RowCheckSize = 14f;
        public const float RowCheckGap = Space6;
        public const float RowGap = Space4;
        public const float RowFieldPadding = Spacing;
        public const float RowColorWidth = 36f;
        public const float RowWideValueWidth = 48f;
        public const float RowMinValueWidth = 30f;
        public const float RowMinLabelWidth = 64f;
        public const float RowLinkPad = Space8;
        public const float RowDropdownTextInset = Space6;
        public const float RowDropdownArrowInset = 16f;
        public const float RowArrowSize = 12f;
        public const float RowGlyphInset = 1f;
        public const float RowCheckGlyphSize = 12f;
        public const float RowCheckGlyphWeight = 1.8f;
        /// <summary>글리프 상자를 항목 가운데에 놓을 때 쓰는 절반 값(12의 절반).</summary>
        public const float RowArrowHalf = 6f;
        public const float RowMenuDashInset = 3f;
        public const float RowMenuDashDrop = Space6;
        public const float RowMenuDashWidth = Space8;
        public const float RowMenuDashHeight = Spacing;

        // Row anatomy, part two: the list/chip/icon rows in MingInspectorPrimitives and
        // MingDisclosure. Same rule as the block above - a move into one file, no change.
        public const float SummaryChipHeight = 16f;
        public const float ChangedDotSize = 6f;
        public const float NameMinimum = 48f;
        public const float ListPadLeft = 7f;
        public const float ListPadRight = 5f;
        public const float ListChevron = 8f;
        public const float ChipPadLeft = 5f;
        public const float ChipPadRight = 8f;
        public const float SummaryChipPad = 6f;
        /// <summary>목록 GUIStyle의 위·아래 여백(RectOffset이 int를 받는다).</summary>
        public const int ListRowMargin = 3;
        /// <summary>공유 행에서 글자가 시작하는 안쪽 값.</summary>
        public const float SharedRowTextInset = 11f;
        /// <summary>접힘 화살표 글리프의 선 굵기.</summary>
        public const float ChevronGlyphWeight = 1.6f;
        /// <summary>체크박스 면의 모서리.</summary>
        public const float RowCheckRadius = 2f;
        /// <summary>혼합값 대시의 좌우 안쪽 값과 두께.</summary>
        public const float RowMixedDashInset = 2f;
        public const float RowMixedDashHeight = 1.5f;
        /// <summary>진행 막대의 최대 너비, 두께, 모서리.</summary>
        public const float ProgressBarMaxWidth = 30f;
        public const float ProgressBarHeight = 4f;
        public const float ProgressBarRadius = 2f;
        /// <summary>진행 막대를 세로 가운데에 놓을 때 쓰는 절반 값(4의 절반).</summary>
        public const float ProgressBarHalf = 2f;
        /// <summary>클릭 영역을 사방으로 넓히는 값과 그 두 배(가로·세로 합).</summary>
        public const float HitPad = 2f;
        public const float HitPadTotal = 4f;
        /// <summary>선택 탭 밑줄이 탭 면에서 좌우로 비우는 값.</summary>
        public const float TabPlateInset = 2f;
        public const float TabPlateWidthTrim = 4f;
        /// <summary>목록 행 라벨의 최소 너비와 차지하는 비율.</summary>
        public const float ListRowLabelMinWidth = 56f;
        public const float ListRowLabelWidthRatio = 0.28f;
        /// <summary>세그먼트 줄이 칸 사이에 두는 최소·최대 여백.</summary>
        public const float SegmentMinPad = 2f;
        public const float SegmentMaxPad = 5f;
        /// <summary>아주 좁은 범위 슬라이더(0.1 폭)를 판별하는 값.</summary>
        public const float TinyRange = 0.1f;
        /// <summary>점선 테두리의 한 획 길이와 사이 값.</summary>
        public const float DashLength = 3f;
        public const float DashGap = 2f;

        // ------------------------------------------------- pinned material header
        // The strip above the inspector (MingPinnedMaterialHeader.cs and its
        // Toolbar/NavStrip parts) plus the search popup in MingToonShaderGUI.InspectorUx.
        // Widths, paddings and radii used to sit inline at the call sites.
        /// <summary>이 폭 아래를 좁은 창으로 본다.</summary>
        public const float PinnedNarrowWidth = 360f;
        /// <summary>창 폭을 알 수 없을 때의 대체 폭.</summary>
        public const float PinnedFallbackWidth = 400f;
        /// <summary>툴바 전체 높이(두 줄)와 한 줄 높이.</summary>
        public const float PinnedToolbarHeight = 68f;
        public const float PinnedToolbarRowHeight = 20f;
        /// <summary>스티키 띠가 놓이는 위쪽 여백.</summary>
        public const float PinnedStickyReleaseBand = 8f;
        /// <summary>공유 헤더 내용의 좌·우 안쪽 값.</summary>
        public const float PinnedContentInsetLeft = 20f;
        public const float PinnedContentInsetRight = 24f;
        /// <summary>툴바 컨테이너의 좌우·위아래 여백.</summary>
        public const float PinnedContainerPadSide = 4f;
        public const float PinnedContainerPadVertical = 3f;
        /// <summary>언어 라벨의 좌·우 여백.</summary>
        public const float PinnedLanguageLabelMarginRight = 2f;
        public const float PinnedLanguageLabelMarginLeft = 5f;
        /// <summary>언어 드롭다운의 폭(보통·좁은 창)과 최소 폭.</summary>
        public const float PinnedLanguageDropdownWidth = 74f;
        public const float PinnedLanguageDropdownNarrowWidth = 76f;
        public const float PinnedLanguageDropdownMinWidth = 60f;
        /// <summary>툴바 버튼의 좌우 안쪽 값.</summary>
        public const float PinnedButtonPadSide = 3f;
        /// <summary>검색 칸이 차지하는 flex 비율.</summary>
        public const float PinnedSearchFieldFlexGrow = 2f;
        /// <summary>비용 게이지의 두께와, 비용 표시(게이지·등급 배지)의 왼쪽 여백·모서리.</summary>
        public const float PinnedCostGaugeHeight = 6f;
        public const float PinnedCostMarginLeft = 4f;
        public const float PinnedCostChipRadius = 3f;
        /// <summary>툴바에서 칸과 칸 사이에 두는 값과, 좁은 창·인라인 툴바에서의 값.</summary>
        public const float PinnedCellGap = 2f;
        public const float PinnedCellGapNarrow = 1f;
        /// <summary>검색 지우기 버튼의 위치와 크기·모서리.</summary>
        public const float PinnedClearButtonRight = 5f;
        public const float PinnedClearButtonTop = 3f;
        public const float PinnedClearButtonSize = 13f;
        public const float PinnedClearButtonRadius = 7f;
        /// <summary>필터 버튼 한 칸의 폭(보통·좁은 창)과 안쪽 여백·오른쪽 여백.</summary>
        public const float PinnedFilterCellWidth = 58f;
        public const float PinnedFilterCellNarrowWidth = 48f;
        public const float PinnedFilterPadSide = 3f;
        /// <summary>전체 불러오기 버튼의 폭과 안쪽·왼쪽 여백.</summary>
        public const float PinnedFullLoadButtonWidth = 78f;
        public const float PinnedFullLoadButtonPad = 2f;
        /// <summary>동작 버튼 한 칸의 폭(보통·좁은 창).</summary>
        public const float PinnedActionCellWidth = 24f;
        public const float PinnedActionCellNarrowWidth = 21f;
        /// <summary>툴바 아이콘의 크기.</summary>
        public const float PinnedIconSize = 14f;
        /// <summary>검색 아이콘의 안쪽 값과 크기, 글자가 시작하는 값, 지우기 버튼이 비우는 값.</summary>
        public const float PinnedSearchIconInset = 3f;
        public const float PinnedSearchIconSize = 13f;
        public const float PinnedSearchTextInset = 18f;
        public const float PinnedSearchClearInset = 20f;
        /// <summary>툴바 줄을 그릴 때의 좌우 여백.</summary>
        public const float PinnedToolbarRowInset = 32f;
        /// <summary>검색 칸의 최소 폭과 언어 라벨·툴바 버튼의 최소 폭.</summary>
        public const float PinnedSearchFieldMinWidth = 96f;
        public const float PinnedLanguageLabelMinWidth = 42f;
        public const float PinnedToolbarButtonMinWidth = 74f;
        /// <summary>측정한 컨트롤에 더하는 여유.</summary>
        public const float PinnedMeasuredControlPad = 4f;
        /// <summary>검색 팝업의 최소 폭·최대·최소 높이.</summary>
        public const float PinnedSearchPopupMinWidth = 320f;
        public const float PinnedSearchPopupMaxHeight = 260f;
        public const float PinnedSearchPopupMinHeight = 36f;
        /// <summary>검색 팝업의 검색 줄 높이와 안쪽 여백, 결과 행 높이.</summary>
        public const float PinnedSearchPopupSearchHeight = 24f;
        public const float PinnedSearchPopupSearchPadding = 4f;
        public const float PinnedSearchResultHeight = 22f;
        /// <summary>검색 팝업 목록의 위·아래 여백, 스크롤바 폭, 내용 좌우 여백.</summary>
        public const float PinnedSearchPopupListPad = 8f;
        public const float PinnedSearchPopupScrollbarWidth = 16f;
        public const float PinnedSearchPopupContentPad = 4f;
        /// <summary>클립보드 버튼의 폭(보통·좁은 창).</summary>
        public const float PinnedClipboardButtonWidth = 24f;
        public const float PinnedClipboardButtonNarrowWidth = 20f;
        /// <summary>nav 한 줄의 높이와 버튼 최소 폭·사이 여백·위아래 여백.</summary>
        public const float PinnedNavRowHeight = 24f;
        public const float PinnedNavButtonMinWidth = 14f;
        public const float PinnedNavButtonSpacing = 2f;
        public const float PinnedNavButtonGap = 4f;
        public const float PinnedNavButtonMargin = 2f;
        /// <summary>nav 캡션 글자 크기(nav 버튼).</summary>
        public const int PinnedNavFontSize = 10;
        /// <summary>스티키 줄의 높이·위 여백·오른쪽 여백과 레일 폭·오른쪽 여백.</summary>
        public const float PinnedStickyRowHeight = 20f;
        public const float PinnedStickyRowGap = 2f;
        public const float PinnedStickyRowPaddingRight = 6f;
        public const float PinnedStickyRailWidth = 3f;
        public const float PinnedStickyRailMarginRight = 6f;
        /// <summary>게이트 행 액센트 레일의 안쪽 값과 폭.</summary>
        public const float PinnedGateRowAccentInset = 5f;
        public const float PinnedGateRowAccentWidth = 3f;
        /// <summary>좁은 창에서 검색 칸이 지키는 최소 폭과 줄어든 여백.</summary>
        public const float PinnedNarrowSearchFieldMinWidth = 72f;
        public const float PinnedNarrowSearchFieldGap = 4f;
        /// <summary>좁은 창에서 필터 버튼 한 칸의 폭.</summary>
        public const float PinnedNarrowFilterWidth = 44f;
        /// <summary>인라인 툴바의 설명서 도움말 버튼 폭.</summary>
        public const float PinnedHelpButtonWidth = 24f;
        /// <summary>언어 드롭다운이 줄에서 차지하는 비율과, 라벨이 지키는 최소·예약 폭.</summary>
        public const float PinnedLanguagePopupWidthRatio = 0.24f;
        public const float PinnedLanguageLabelFloor = 20f;
        public const float PinnedLanguageLabelReserve = 108f;
        /// <summary>검색 칸 안쪽의 지우기 단추 위치와 폭.</summary>
        public const float PinnedSearchCancelInset = 16f;
        public const float PinnedSearchCancelWidth = 15f;
        /// <summary>검색 팝업의 검색 줄 안쪽 여백(좌·우·위).</summary>
        public const float PinnedSearchPopupSearchPadLeft = 8f;
        public const float PinnedSearchPopupSearchPadRight = 5f;
        public const float PinnedSearchPopupSearchTop = 3f;
        /// <summary>레이어 사용량 배지 줄의 높이와 칸 사이.</summary>
        public const float LayerBadgeRowHeight = 28f;
        public const float LayerBadgeGap = Space4;

        // ------------------------------------------- texture module cards
        // MingTextureModuleGUI.TextureCards: the texture box (stage 3, approved mockup
        // fm_extra.css .tbox) and the compact slider that sits inside it.
        public const float BoxPreviewSize = 64f;
        public const float BoxPreviewNarrowSize = 48f;
        public const float MaskBoxPreviewSize = 44f;
        /// <summary>이 안쪽 폭 아래면 박스를 좁은 상태로 본다.</summary>
        public const float BoxNarrowWidth = 240f;
        /// <summary>박스 상세(폴드아웃) 본문이 한 단계 더 들어가는 값.</summary>
        public const float BoxFoldIndent = 24f;
        /// <summary>박스 머리의 혼합(블렌드) 드롭다운 최대 폭.</summary>
        public const float BoxBlendMaxWidth = 86f;
        /// <summary>박스 안 값 칸(필드)의 기본 폭.</summary>
        public const float BoxValueFieldWidth = 48f;
        /// <summary>박스 머리 메뉴 버튼이 오른쪽에서 비우는 값.</summary>
        public const float BoxMenuInset = 3f;
        /// <summary>박스 머리 드롭다운의 최소 폭과 이름에 남겨 두는 폭.</summary>
        public const float BoxBlendMinWidth = 24f;
        public const float BoxBlendNameReserve = 48f;
        /// <summary>텍스처 카드 사이의 가로·세로 여백.</summary>
        public const float CardGap = 6f;
        public const float CardRowGap = 4f;
        /// <summary>카드가 지키는 최소 폭과, 첫 측정 전 뷰 폭에서 빼는 값.</summary>
        public const float CardMinWidth = 140f;
        public const float CardViewInset = 64f;
        /// <summary>카드 라벨과 값 사이에 더하는 여백.</summary>
        public const float CardLabelPad = 2f;
        /// <summary>상세 행이 지키는 최소 폭.</summary>
        public const float CardDetailMinWidth = 48f;
        /// <summary>텍스처 카드에 붙는 상세 행의 안쪽 들여쓰기.</summary>
        public const float TextureAttachedDetailInset = 16f;
        /// <summary>텍스처 카드에 붙는 상세 행의 작은 헤더 높이.</summary>
        public const float TextureAttachedDetailHeaderHeight = 18f;
        /// <summary>텍스처 카드에 붙는 상세 행의 연결선 시작 여백.</summary>
        public const float TextureAttachedDetailConnectorInset = 8f;
        /// <summary>텍스처 카드에 붙는 상세 행의 연결선 폭.</summary>
        public const float TextureAttachedDetailConnectorWidth = 1f;
        /// <summary>카드 값 칸이 지키는 최소 폭과 오른쪽에 남기는 폭.</summary>
        public const float CardFieldValueMinWidth = 12f;
        public const float CardFieldValueReserve = 24f;
        /// <summary>카드 안 구분선이 차지하는 높이와 좌우로 비우는 값.</summary>
        public const float CardDividerHeight = 4f;
        public const float CardDividerInset = 2f;
        /// <summary>스케일/오프셋 두 줄 사이의 값.</summary>
        public const float CardScaleOffsetPad = 2f;
        /// <summary>컴팩트 슬라이더의 드래그 트랙 최소 폭·숫자 칸 폭·사이 값.</summary>
        public const float CompactSliderTrackMinimum = 35f;
        public const float CompactSliderNumericWidth = 34f;
        public const float CompactSliderGap = 3f;
        /// <summary>HSVG 한 칸 슬라이더의 숫자 칸 폭.</summary>
        public const float HsvgNumericWidth = 30f;
        /// <summary>미리보기 안쪽 여백·아이콘 크기(보통·좁음)·라벨 높이.</summary>
        public const float PreviewInset = 2f;
        public const float PreviewIconSize = 16f;
        public const float PreviewIconSizeNarrow = 14f;
        public const float PreviewLabelHeight = 14f;
        /// <summary>미리보기 아이콘 버튼 안 글리프의 최대 크기와 좌우 여백.</summary>
        public const float PreviewGlyphMaxSize = 10f;
        public const float PreviewGlyphGap = 4f;
        /// <summary>상태 점의 지름·모서리·세로 중심에서의 절반 값.</summary>
        public const float StatusDotSize = 6f;
        public const float StatusDotRadius = 3f;
        public const float StatusDotHalf = 3f;
        /// <summary>틸팅/오프셋 줄의 라벨 폭(최소·여유)과 링크 칸 폭·비율.</summary>
        public const float TexturePlacementAxisLabelWidth = 28f;
        public const float TexturePlacementLinkWidth = 24f;
        public const float TexturePlacementLinkMinWidth = 18f;
        public const float TexturePlacementLinkWidthRatio = 0.2f;
        public const float TexturePlacementLabelPad = 5f;
        public const float TexturePlacementLabelMinWidth = 40f;
        public const float TexturePlacementRowGap = 2f;
        public const float TexturePlacementCellsMinWidth = 2f;

        // Texture preview / gallery popup (MingTextureModuleGUI.TexturePreview). The popup
        // lays out at absolute rects, so every y offset and cell size is named here.
        public const float PreviewWindowWidth = 330f;
        public const float PreviewWindowHeight = 365f;
        public const float PreviewGalleryWindowWidth = 354f;
        public const float PreviewGalleryWindowHeight = 780f;
        /// <summary>팝업 내용이 좌우로 비우는 값.</summary>
        public const float PreviewInsetX = 8f;
        public const float PreviewTitleY = 5f;
        public const float PreviewTextRowHeight = 18f;
        public const float PreviewLargeY = 27f;
        public const float PreviewLargeHeight = 296f;
        public const float PreviewLargeHeightPlain = 304f;
        public const float PreviewInfoY = 327f;
        public const float PreviewInfoYPlain = 337f;
        public const float PreviewGalleryHeaderY = 349f;
        public const float PreviewGalleryGridY = 372f;
        public const float PreviewGalleryGridHeight = 220f;
        public const float PreviewImportY = 598f;
        public const float PreviewImportHeight = 136f;
        public const float PreviewHintY = 744f;
        public const float PreviewHintHeight = 32f;
        /// <summary>갤러리 탐색 줄의 높이와 아래 그리드가 시작하는 y.</summary>
        public const float GalleryNavHeight = 54f;
        public const float GalleryGridTop = 58f;
        /// <summary>갤러리 격자의 스크롤 여유, 칸 높이, 칸 사이, 미리보기 높이.</summary>
        public const float GalleryColumnGap = 17f;
        public const float GalleryRowHeight = 82f;
        public const float GalleryCellGap = 3f;
        public const float GalleryThumbHeight = 58f;
        public const float GalleryCaptionInset = 2f;
        public const float GalleryCaptionY = 62f;
        public const float GalleryCaptionHeight = 16f;
        /// <summary>갤러리 탐색 줄의 요소 사이·위로 가기 버튼 폭·검색 칸 예약 폭.</summary>
        public const float GalleryNavGap = 3f;
        public const float GalleryUpWidth = 34f;
        public const float GallerySearchReserve = 88f;
        /// <summary>경로 조각 버튼의 최소 폭과 안쪽 여백, 구분자 폭.</summary>
        public const float GalleryCrumbMinWidth = 22f;
        public const float GalleryCrumbPad = 8f;
        public const float GallerySeparatorWidth = 9f;
        /// <summary>하위 폴더 줄의 라벨 폭·다음 요소까지의 거리·버튼 최소 폭.</summary>
        public const float GalleryFolderLabelWidth = 35f;
        public const float GalleryFolderOffset = 38f;
        public const float GalleryFolderMinWidth = 46f;
        /// <summary>넘침 버튼의 폭과, 그 버튼을 그릴 최소 남은 폭, 넘침 메뉴 폭.</summary>
        public const float GalleryOverflowWidth = 28f;
        public const float GalleryOverflowMinVisible = 20f;
        public const float GalleryOverflowMenuWidth = 20f;
        public const float GalleryButtonGap = 2f;
        /// <summary>미리보기에서 고르기 버튼 자리를 비워 두는 정사각 크기.</summary>
        public const float PreviewClearSize = 22f;

        // ------------------------------------------- texture module: masks and slots
        // MingTextureModuleGUI.MaskCards / .CompactSlot
        /// <summary>효과 칸을 두 열로 묶는 기준 폭(이보다 넓으면 2열).</summary>
        public const float MaskBoxPairedWidth = 250f;
        /// <summary>입력 레벨 줄의 숫자 칸·대시 칸 폭과 사이 값.</summary>
        public const float LevelsNumericWidth = 38f;
        public const float LevelsDashWidth = 12f;
        public const float LevelsGap = 3f;
        /// <summary>입력 레벨 그라디언트 띠의 두께.</summary>
        public const float LevelsStripHeight = 6f;
        /// <summary>컴팩트 슬롯의 안쪽 여백과 혼합값 대시의 폭·두께.</summary>
        public const float CompactSlotInset = 2f;
        public const float CompactSlotDashWidth = 8f;
        public const float CompactSlotDashHeight = 2f;

        // ------------------------------------------- texture import (batch) panel
        // MingTextureImportSettingsGUI.Batch
        /// <summary>일괄 임포트 패널이 좁은 상태로 접히는 폭.</summary>
        public const float BatchNarrowWidth = 440f;
        /// <summary>선택 요약 라벨의 최소·최대 폭.</summary>
        public const float BatchSummaryMinWidth = 80f;
        public const float BatchSummaryMaxWidth = 180f;
        /// <summary>표 머리에서 첫 열 앞에 두는 들여쓰기.</summary>
        public const float BatchLabelIndent = 86f;
        /// <summary>표 열 폭: 그룹 라벨·최대 해상도 / PC 형식 / 압축 품질.</summary>
        public const float BatchMaxSizeWidth = 78f;
        public const float BatchFormatWidth = 90f;
        public const float BatchCompressionWidth = 86f;

        // ------------------------------------------------- inspector: remaining chrome
        /// <summary>전체 모드 검색 칸의 필터 버튼 폭·안쪽 값, 지우기 버튼 폭과 글리프.</summary>
        public const float FullSearchFilterWidth = 18f;
        public const float FullSearchFilterInset = 3f;
        public const float FullSearchClearWidth = 14f;
        public const float FullSearchClearGlyphInset = 3f;
        public const float FullSearchClearGlyphSize = 8f;
        /// <summary>림 범위 줄의 반전 칸 폭·사이 값과 트랙·숫자 칸 최소 폭.</summary>
        public const float RimRangeInvertWidth = 60f;
        public const float RimRangeInvertGap = 4f;
        public const float RimRangeTrackMinimum = 35f;
        public const float RimRangeNumericMinimum = 45f;
        public const float RimRangeControlGap = 3f;
        public const float RimRangeLabelPad = 8f;
        /// <summary>텍스처 목록 뷰의 정렬 라벨·할당 필터 폭과 목록 요소 크기.</summary>
        public const float TextureViewSortLabelWidth = 28f;
        public const float TextureViewAssignedOnlyWidth = 95f;
        public const float TextureViewSectionSpace = 5f;
        public const float TextureViewThumbnailSize = 42f;
        public const float TextureViewTitleHeight = 24f;
        public const float TextureViewArrowWidth = 32f;
        public const float TextureViewLabelRightInset = 36f;
        public const int TextureViewExpandFontSize = 16;
        /// <summary>간단 모드 섹션 머리 높이와 캐릭터 높이 줄 높이.</summary>
        public const float SimpleSectionHeaderHeight = 28f;
        public const float CharacterHeightRowHeight = 28f;
        /// <summary>빠른 조절 슬라이더 카드에서 라벨이 지키는 최소 폭.</summary>
        public const float QuickSliderMinLabelWidth = 110f;
        /// <summary>설명서 링크 아이콘 버튼의 폭·높이.</summary>
        public const float DocsLinkIconWidth = 24f;
        public const float DocsLinkIconHeight = 16f;
        /// <summary>속성 되돌리기 열의 폭·단추 크기·사이 값·글리프 크기.</summary>
        public const float ResetColumnWidth = 18f;
        public const float ResetButtonSize = 16f;
        public const float ResetButtonGap = 2f;
        public const int ResetGlyphFontSize = 12;
        /// <summary>베이크 후 편집 유지 표시의 테두리 두께.</summary>
        public const float BakeKeepEdgeWidth = 2f;
        /// <summary>전체 모드 룩 카드의 드롭다운 최소 폭과 요소 사이 값.</summary>
        public const float FullLookDropdownMinimum = 64f;
        public const float FullLookGap = 4f;
        /// <summary>토글 버튼 GUIStyle의 좌우·위아래 여백.</summary>
        public const int ToggleButtonMarginSide = 2;
        public const int ToggleButtonMarginVertical = 1;

        // ---------------------------------------------------------- MingToon Manager
        // The Manager inspector (Editor/Baking/MingToonManagerEditor*.cs). Values
        // are the ones the partials shipped with; the 2026-09-30 pass moved them
        // here unchanged (owner: "밍툰매니저쪽도 다 테마 토큰화").
        /// <summary>섹션 동작 버튼(변환·얼굴 노멀·높이·윤곽선 베이크) 높이.</summary>
        public const float ManagerActionButtonHeight = 30f;
        /// <summary>수동 베이크 버튼 높이(주 버튼 한 단계 위).</summary>
        public const float ManagerBakeButtonHeight = ButtonHero;
        /// <summary>시작 탭에서 "이 모양 적용"을 보조 버튼으로 그릴 때의 높이.</summary>
        public const float ManagerReapplyButtonHeight = ButtonLarge;
        /// <summary>높이 그라데이션 재질 적용 버튼 높이.</summary>
        public const float ManagerHeightMaterialButtonHeight = 28f;
        /// <summary>슬롯 행의 역할·얼굴 모드 드롭다운 폭.</summary>
        public const float ManagerSlotDropdownWidth = 112f;
        /// <summary>얼굴 슬롯 목록 스크롤의 최대 높이.</summary>
        public const float ManagerFaceSlotListMaxHeight = 320f;
        /// <summary>"선택에서 추가" 미니 버튼 폭.</summary>
        public const float ManagerAddSelectionWidth = 140f;
        /// <summary>출력 폴더 고르기·되돌리기 버튼 폭.</summary>
        public const float ManagerOutputPickWidth = 64f;
        public const float ManagerOutputResetWidth = 24f;
        /// <summary>UV4 점유 렌더러 행의 "찾기" 버튼 폭.</summary>
        public const float ManagerReadinessPingWidth = 48f;
        /// <summary>변환 복구 버튼 위 간격.</summary>
        public const float ManagerRoundTripGap = 3f;
        /// <summary>높이 베이크 경고 구분선 아래 간격(위는 Space6).</summary>
        public const float ManagerWarningRuleGapAfter = 3f;
        /// <summary>선택 칩 폭 = 글자 폭 + 이 값.</summary>
        public const float ManagerSelectionChipPad = Space12;
        /// <summary>줄바꿈 토글의 최소 폭과 오른쪽에 남기는 폭.</summary>
        public const float ManagerWrappedToggleMinWidth = 80f;
        public const float ManagerWrappedToggleRightReserve = 60f;
        /// <summary>변환 로그: 복사 버튼 폭, 필터 라벨·지우기 폭, 스크롤 최대 높이, 아이콘 크기.</summary>
        public const float ManagerLogCopyWidth = 92f;
        public const float ManagerLogFilterLabelWidth = 58f;
        public const float ManagerLogClearWidth = 54f;
        public const float ManagerLogMaxHeight = 360f;
        public const float ManagerLogIconSize = 18f;
        /// <summary>투명 파츠 순서: 앞/뒤 버튼 폭과 순서 값 폭.</summary>
        public const float DrawOrderStepButtonWidth = 26f;
        public const float DrawOrderValueWidth = 34f;
        /// <summary>업데이트 알림 줄: 상자 안쪽 값, 버튼·토글 폭, 버전 글자 최소 폭.</summary>
        public const float UpdateRowHeightPad = 4f;
        public const float UpdateRowInsetX = 4f;
        public const float UpdateRowInsetY = 2f;
        public const float UpdateRowInsetTotal = 8f;
        public const float UpdateRowActionWidth = 56f;
        public const float UpdateRowNotesStep = 62f;
        public const float UpdateRowNotesWidth = 60f;
        public const float UpdateRowToggleMaxWidth = 118f;
        public const float UpdateRowToggleMinWidth = 20f;
        public const float UpdateRowGap = Space4;
        public const float UpdateRowVersionTrim = Space6;
        public const float UpdateRowVersionMinWidth = 24f;
        /// <summary>생성 캐시 정리 창: 최소 크기, 버튼 높이, 상세 스크롤 높이, 구역 간격.</summary>
        public const float GeneratedCacheWindowMinWidth = 430f;
        public const float GeneratedCacheWindowMinHeight = 460f;
        public const float GeneratedCacheButtonHeight = 25f;
        public const float GeneratedCacheDetailMinHeight = 100f;
        public const float GeneratedCacheDetailMaxHeight = 240f;
        public const float GeneratedCacheSectionGap = 5f;

        // ---------------------------------------------------------- material cost panel
        /// <summary>작은 비용 표시: 게이지 폭, 퍼센트 폭, 사이 값, 퍼센트 글자 크기.</summary>
        public const float CostCompactGaugeWidth = 72f;
        public const float CostCompactPercentWidth = 32f;
        public const float CostCompactGaugeGap = Space6;
        public const int CostCompactFontSize = 9;
        /// <summary>비용 게이지 두께·모서리, 가운데 정렬용 절반 값.</summary>
        public const float CostGaugeHeight = 6f;
        public const float CostGaugeRadius = 3f;
        public const float CostGaugeHalf = 3f;
        /// <summary>추정 카드의 값 줄 높이, 표 값 열 폭.</summary>
        public const float CostEstimateValueHeight = 22f;
        public const float CostTableValueWidth = 110f;

        // ---------------------------------------------------------- setup wizard
        /// <summary>머리 줄의 도움말 단추 크기와 언어 드롭다운이 비워 두는 폭.</summary>
        public const float SetupHeaderHelpSize = 18f;
        public const float SetupHeaderHelpReserve = 22f;
        /// <summary>창 배경을 카드 위·아래로 넘겨 칠하는 값(위, 합계).</summary>
        public const float SetupSurfaceBleedTop = 2f;
        public const float SetupSurfaceBleedTotal = 6f;
        /// <summary>이 폭 아래를 좁은 머리 줄로 본다.</summary>
        public const float SetupNarrowHeaderWidth = 350f;
        /// <summary>출력 폴더 경로 글자 칸 높이.</summary>
        public const float SetupFolderLabelHeight = 36f;
        /// <summary>목록 행(ListRow) 안에서 글자 줄(Line)을 내려 놓는 값.</summary>
        public const float SetupListRowTextInset = 2f;

        // ---------------------------------------------------------- update window
        /// <summary>업데이트 창 최소 크기(전체 / 자동 알림), 정보 줄 라벨 폭.</summary>
        public const float UpdateWindowMinWidth = 430f;
        public const float UpdateWindowMinHeight = 300f;
        public const float UpdateNoticeMinWidth = 360f;
        public const float UpdateNoticeMinHeight = 180f;
        public const float UpdateWindowLabelWidth = 132f;
        public const float UpdateWindowScrollbarReserve = 18f;

        // font sizes (Unity type scale; tokens.json type styles)
        public const int FontHero = 19;
        public const int FontTitle = 14;
        public const int FontSection = 14;
        public const int FontBody = 12;
        public const int FontSmall = 11;
        public const int FontMini = 10;

        /// <summary>Unity's default inspector label width for a given context width.</summary>
        public static float LabelWidth(float contextWidth)
        {
            return Mathf.Max(contextWidth * 0.45f - 40f, LabelMin);
        }
    }

    public static class SRTheme
    {
        private static SRPalette dark;
        private static SRPalette light;

        /// <summary>Neighbouring nav buttons hold their accent at this alpha when not the active group.</summary>
        public const float NavAccentRestAlpha = 0.34f;
        /// <summary>Cost gauge fill holds the rating colour at this alpha.</summary>
        public const float CostGaugeFillAlpha = 0.9f;
        /// <summary>GUI.color alpha multiplier for a row that is being held back.</summary>
        public const float DimmedAlpha = 0.55f;

        public static bool Dark { get { return EditorGUIUtility.isProSkin; } }

        /// <summary>Palette of the current editor skin (checked every call; Unity has no skin-change event).</summary>
        public static SRPalette P
        {
            get
            {
                if (Dark)
                {
                    if (dark == null) dark = BuildDark();
                    return dark;
                }
                if (light == null) light = BuildLight();
                return light;
            }
        }

        public static SRToneColors Tone(SRTone tone)
        {
            SRPalette p = P;
            switch (tone)
            {
                case SRTone.Brand: return new SRToneColors(p.Brand, p.TextStrong, p.BrandTint, p.BrandBorder);
                case SRTone.Success: return new SRToneColors(p.Success, p.SuccessText, p.SuccessBg, p.SuccessBorder);
                case SRTone.Warning: return new SRToneColors(p.Warning, p.WarningText, p.WarningBg, p.WarningBorder);
                case SRTone.Danger: return new SRToneColors(p.Danger, p.DangerText, p.DangerBg, p.DangerBorder);
                case SRTone.Info: return new SRToneColors(p.Info, p.InfoText, p.InfoBg, p.InfoBorder);
                default: return new SRToneColors(p.LineStrong, p.TextMuted, p.SurfaceSection, p.LineControl);
            }
        }

        public static Color Product(SRProduct product)
        {
            SRPalette p = P;
            switch (product)
            {
                case SRProduct.Physics: return p.ProductPhysics;
                case SRProduct.CharacterToolbox: return p.ProductCtb;
                case SRProduct.MaskMaker: return p.ProductMm;
                case SRProduct.EasyMeshEditor: return p.ProductEme;
                case SRProduct.WarudoBatchBuilder: return p.ProductWbb;
                case SRProduct.OnAirDeck: return p.Mint;
                default: return p.ProductMingtoon;
            }
        }

        // ---------------------------------------------------------------- drawing primitives

        /// <summary>Rounded rectangle face. Repaint only.</summary>
        public static void Fill(Rect rect, Color color, float radius = 0f)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (rect.width <= 0f || rect.height <= 0f || color.a <= 0f) return;
            if (radius <= 0f)
            {
                EditorGUI.DrawRect(rect, color);
                return;
            }
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, color, 0f, radius);
        }

        /// <summary>Rounded rectangle outline. Repaint only.</summary>
        public static void Stroke(Rect rect, Color color, float radius = 0f, float width = 1f)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (rect.width <= 0f || rect.height <= 0f || color.a <= 0f) return;
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, color, width, radius);
        }

        public static void Box(Rect rect, Color fill, Color border, float radius)
        {
            Fill(rect, fill, radius);
            Stroke(rect, border, radius, 1f);
        }

        public static string Hex(Color c)
        {
            return "#" + ColorUtility.ToHtmlStringRGB(c);
        }

        /// <summary>
        /// Text colour for text Unity draws itself (field values, popup value, prefix labels) while
        /// GUI.enabled is false. Unity blends that text at half alpha over the face, so this is
        /// 2 * text-disabled - face: the blend lands on text-disabled. Where that is out of range
        /// (light field face) it clamps, and the result stays above the report's 2.6:1 floor
        /// (gen-theme "unity-dim" checks).
        /// </summary>
        public static Color DisabledInk(Color face)
        {
            Color t = P.TextDisabled;
            return new Color(Mathf.Clamp01(2f * t.r - face.r), Mathf.Clamp01(2f * t.g - face.g), Mathf.Clamp01(2f * t.b - face.b), 1f);
        }

        // ---------------------------------------------------------------- palette (generated)

        private static SRPalette BuildDark()
        {
            var p = new SRPalette();
            p.SurfaceApp = new Color32(34, 25, 30, 255);
            p.SurfaceWindow = new Color32(62, 53, 59, 255);
            p.SurfaceCard = new Color32(70, 61, 67, 255);
            p.SurfaceSection = new Color32(77, 68, 74, 255);
            p.SurfaceHover = new Color32(85, 75, 81, 255);
            p.SurfaceSunken = new Color32(46, 40, 44, 255);
            // The inspector's own row tokens: the tap header keeps the section
            // tone, and the zebra band is the window tone so a striped body row
            // can never read as a header.
            p.HeaderBand = p.SurfaceSection;
            p.HeaderBandHover = p.SurfaceHover;
            p.HeaderRule = new Color32(58, 49, 55, 255);
            p.RowBand = p.SurfaceWindow;
            p.Control = new Color32(94, 85, 90, 255);
            p.ControlHover = new Color32(102, 93, 99, 255);
            p.ControlPressed = new Color32(96, 72, 81, 255);
            p.LineSubtle = new Color32(45, 39, 43, 255);
            p.LineControl = new Color32(36, 31, 34, 255);
            p.LineStrong = new Color32(155, 146, 151, 255);
            p.TextStrong = new Color32(244, 238, 236, 255);
            p.Text = new Color32(213, 207, 210, 255);
            p.TextMuted = new Color32(185, 178, 182, 255);
            p.TextDisabled = new Color32(150, 144, 148, 255);
            p.Brand = new Color32(215, 154, 170, 255);
            p.BrandHover = new Color32(227, 168, 183, 255);
            p.BrandPressed = new Color32(202, 139, 156, 255);
            p.BrandBorder = new Color32(184, 118, 136, 255);
            p.BrandLine = new Color32(215, 154, 170, 255);
            p.TextOnBrand = new Color32(36, 23, 29, 255);
            p.BrandText = new Color32(229, 171, 186, 255);
            p.Mint = new Color32(57, 197, 187, 255);
            p.Focus = new Color32(57, 197, 187, 255);
            p.Link = new Color32(57, 197, 187, 255);
            p.BrandTint = new Color32(96, 76, 84, 255);
            p.Success = new Color32(111, 208, 135, 255);
            p.SuccessText = new Color32(147, 228, 164, 255);
            p.SuccessBg = new Color32(71, 73, 70, 255);
            p.SuccessBorder = new Color32(90, 124, 95, 255);
            p.Warning = new Color32(255, 193, 7, 255);
            p.WarningText = new Color32(251, 205, 104, 255);
            p.WarningBg = new Color32(86, 71, 63, 255);
            p.WarningBorder = new Color32(149, 117, 65, 255);
            p.Danger = new Color32(255, 83, 74, 255);
            p.DangerText = new Color32(253, 161, 154, 255);
            p.DangerBg = new Color32(88, 60, 63, 255);
            p.DangerBorder = new Color32(151, 73, 70, 255);
            p.Info = new Color32(115, 186, 234, 255);
            p.InfoText = new Color32(151, 207, 247, 255);
            p.InfoBg = new Color32(71, 70, 81, 255);
            p.InfoBorder = new Color32(90, 114, 138, 255);
            p.ProductMingtoon = new Color32(215, 154, 170, 255);
            p.ProductPhysics = new Color32(57, 197, 187, 255);
            p.ProductCtb = new Color32(77, 204, 255, 255);
            p.ProductMm = new Color32(115, 217, 140, 255);
            p.ProductEme = new Color32(234, 118, 0, 255);
            p.ProductWbb = new Color32(192, 191, 227, 255);
            p.SceneAdd = new Color32(51, 229, 255, 89);
            p.SceneRemove = new Color32(255, 64, 51, 89);
            p.SceneHintBg = new Color32(0, 0, 0, 140);
            p.SceneHintText = new Color32(255, 255, 255, 255);
            p.Scrim = new Color32(16, 19, 20, 115);
            p.PinnedNavFallbackBand = new Color(0.245f, 0.245f, 0.245f, 1.0f);
            p.PinnedToolbarBand = new Color32(56, 56, 56, 255);
            p.PinnedNavSelected = new Color32(69, 96, 125, 255);
            p.NavCaptionOnLightFill = new Color(0.08f, 0.08f, 0.08f, 1.0f);
            p.NavCaptionOnDarkFill = new Color(0.96f, 0.96f, 0.96f, 1.0f);
            p.CostGaugeTrack = new Color(0.0f, 0.0f, 0.0f, 0.22f);
            p.SearchPlaceholder = new Color(0.5f, 0.5f, 0.5f, 0.85f);
            p.SearchSelection = new Color(0.24f, 0.43f, 0.68f, 1.0f);
            p.GateRowAccent = new Color(0.22f, 0.43f, 0.66f, 0.62f);
            p.GalleryCellSelected = new Color(0.22f, 0.46f, 0.68f, 0.7f);
            p.GalleryCellApplied = new Color(0.25f, 0.45f, 0.3f, 0.65f);
            p.BakeKeepTintFace = new Color(0.25f, 0.78f, 1.0f, 0.13f);
            p.BakeKeepTintEdge = new Color(0.25f, 0.78f, 1.0f, 0.85f);
            p.CostGradeLight = new Color(0.45f, 0.70f, 1f);
            p.CostGradeModerate = new Color(0.55f, 0.95f, 0.65f);
            p.CostGradeHeavy = new Color(0.95f, 0.88f, 0.50f);
            p.CostGradeHeaviest = new Color(1f, 0.45f, 0.45f);
            return p;
        }

        private static SRPalette BuildLight()
        {
            var p = new SRPalette();
            p.SurfaceApp = new Color32(187, 179, 184, 255);
            p.SurfaceWindow = new Color32(205, 198, 202, 255);
            p.SurfaceCard = new Color32(216, 209, 213, 255);
            p.SurfaceSection = new Color32(196, 189, 193, 255);
            p.SurfaceHover = new Color32(224, 218, 222, 255);
            p.SurfaceSunken = new Color32(247, 243, 245, 255);
            p.HeaderBand = p.SurfaceSection;
            p.HeaderBandHover = p.SurfaceHover;
            p.HeaderRule = new Color32(176, 169, 173, 255);
            p.RowBand = p.SurfaceWindow;
            p.Control = new Color32(231, 226, 229, 255);
            p.ControlHover = new Color32(239, 235, 238, 255);
            p.ControlPressed = new Color32(225, 192, 204, 255);
            p.LineSubtle = new Color32(175, 168, 173, 255);
            p.LineControl = new Color32(162, 156, 160, 255);
            p.LineStrong = new Color32(103, 97, 101, 255);
            p.TextStrong = new Color32(34, 23, 30, 255);
            p.Text = new Color32(26, 20, 24, 255);
            p.TextMuted = new Color32(76, 69, 74, 255);
            p.TextDisabled = new Color32(111, 106, 109, 255);
            p.Brand = new Color32(215, 154, 170, 255);
            p.BrandHover = new Color32(207, 142, 159, 255);
            p.BrandPressed = new Color32(195, 128, 147, 255);
            p.BrandBorder = new Color32(159, 89, 109, 255);
            p.BrandLine = new Color32(149, 69, 94, 255);
            p.TextOnBrand = new Color32(36, 23, 29, 255);
            p.BrandText = new Color32(120, 50, 73, 255);
            p.Mint = new Color32(0, 112, 106, 255);
            p.Focus = new Color32(0, 112, 106, 255);
            p.Link = new Color32(0, 85, 80, 255);
            p.BrandTint = new Color32(217, 190, 198, 255);
            p.Success = new Color32(33, 118, 60, 255);
            p.SuccessText = new Color32(17, 86, 41, 255);
            p.SuccessBg = new Color32(218, 225, 218, 255);
            p.SuccessBorder = new Color32(134, 174, 140, 255);
            p.Warning = new Color32(132, 91, 0, 255);
            p.WarningText = new Color32(102, 66, 0, 255);
            p.WarningBg = new Color32(231, 221, 213, 255);
            p.WarningBorder = new Color32(183, 158, 122, 255);
            p.Danger = new Color32(177, 12, 12, 255);
            p.DangerText = new Color32(141, 27, 25, 255);
            p.DangerBg = new Color32(242, 215, 213, 255);
            p.DangerBorder = new Color32(217, 132, 121, 255);
            p.Info = new Color32(26, 96, 158, 255);
            p.InfoText = new Color32(5, 73, 128, 255);
            p.InfoBg = new Color32(216, 222, 234, 255);
            p.InfoBorder = new Color32(129, 161, 199, 255);
            p.ProductMingtoon = new Color32(129, 59, 81, 255);
            p.ProductPhysics = new Color32(0, 100, 94, 255);
            p.ProductCtb = new Color32(0, 95, 134, 255);
            p.ProductMm = new Color32(9, 103, 46, 255);
            p.ProductEme = new Color32(146, 63, 0, 255);
            p.ProductWbb = new Color32(56, 48, 120, 255);
            p.SceneAdd = new Color32(51, 229, 255, 89);
            p.SceneRemove = new Color32(255, 64, 51, 89);
            p.SceneHintBg = new Color32(0, 0, 0, 140);
            p.SceneHintText = new Color32(255, 255, 255, 255);
            p.Scrim = new Color32(16, 19, 20, 64);
            p.PinnedNavFallbackBand = new Color(0.808f, 0.808f, 0.808f, 1.0f);
            p.PinnedToolbarBand = new Color32(194, 194, 194, 255);
            p.PinnedNavSelected = new Color32(145, 177, 207, 255);
            p.NavCaptionOnLightFill = new Color(0.08f, 0.08f, 0.08f, 1.0f);
            p.NavCaptionOnDarkFill = new Color(0.96f, 0.96f, 0.96f, 1.0f);
            p.CostGaugeTrack = new Color(0.0f, 0.0f, 0.0f, 0.22f);
            p.SearchPlaceholder = new Color(0.5f, 0.5f, 0.5f, 0.85f);
            p.SearchSelection = new Color(0.24f, 0.43f, 0.68f, 1.0f);
            p.GateRowAccent = new Color(0.18f, 0.40f, 0.65f, 0.42f);
            p.GalleryCellSelected = new Color(0.22f, 0.46f, 0.68f, 0.7f);
            p.GalleryCellApplied = new Color(0.25f, 0.45f, 0.3f, 0.65f);
            p.BakeKeepTintFace = new Color(0.25f, 0.78f, 1.0f, 0.13f);
            p.BakeKeepTintEdge = new Color(0.25f, 0.78f, 1.0f, 0.85f);
            p.CostGradeLight = new Color(0.45f, 0.70f, 1f);
            p.CostGradeModerate = new Color(0.55f, 0.95f, 0.65f);
            p.CostGradeHeavy = new Color(0.95f, 0.88f, 0.50f);
            p.CostGradeHeaviest = new Color(1f, 0.45f, 0.45f);
            return p;
        }
    }

    /// <summary>Cached text styles. Rebuilt when the editor skin changes.</summary>
    public static class SRStyles
    {
        private static bool builtForDark;
        private static bool built;
        private static GUIStyle body, muted, mutedWrap, strong, title, section, small, mini, hero, center, buttonText, fieldText, popupText, wrap;
        private static GUIStyle helpLayout, helpActionLayout;

        public static GUIStyle Body { get { Ensure(); return body; } }
        public static GUIStyle Muted { get { Ensure(); return muted; } }
        /// <summary>Muted caption that wraps. Long GUILayout captions must use this: a non-wrapping label widens the whole layout column past the inspector.</summary>
        public static GUIStyle MutedWrap { get { Ensure(); return mutedWrap; } }
        public static GUIStyle Strong { get { Ensure(); return strong; } }
        public static GUIStyle Title { get { Ensure(); return title; } }
        public static GUIStyle Section { get { Ensure(); return section; } }
        public static GUIStyle Small { get { Ensure(); return small; } }
        public static GUIStyle Mini { get { Ensure(); return mini; } }
        public static GUIStyle Hero { get { Ensure(); return hero; } }
        public static GUIStyle Center { get { Ensure(); return center; } }
        public static GUIStyle ButtonText { get { Ensure(); return buttonText; } }
        public static GUIStyle FieldText { get { Ensure(); return fieldText; } }
        public static GUIStyle PopupText { get { Ensure(); return popupText; } }
        public static GUIStyle Wrap { get { Ensure(); return wrap; } }
        /// <summary>Wrap + help box padding. For GUILayoutUtility.GetRect only.</summary>
        public static GUIStyle HelpLayout { get { Ensure(); return helpLayout; } }
        /// <summary>HelpLayout with room for the mini action button below the text.</summary>
        public static GUIStyle HelpActionLayout { get { Ensure(); return helpActionLayout; } }

        private static void Ensure()
        {
            if (built && builtForDark == SRTheme.Dark) return;
            built = true;
            builtForDark = SRTheme.Dark;
            SRPalette p = SRTheme.P;

            body = Text(EditorStyles.label, p.Text, SRMetrics.FontBody, FontStyle.Normal);
            muted = Text(EditorStyles.label, p.TextMuted, SRMetrics.FontSmall, FontStyle.Normal);
            mutedWrap = Text(EditorStyles.label, p.TextMuted, SRMetrics.FontSmall, FontStyle.Normal);
            mutedWrap.wordWrap = true;
            strong =Text(EditorStyles.label, p.TextStrong, SRMetrics.FontBody, FontStyle.Bold);
            title = Text(EditorStyles.label, p.TextStrong, SRMetrics.FontTitle, FontStyle.Bold);
            section = Text(EditorStyles.label, p.TextStrong, SRMetrics.FontSection, FontStyle.Bold);
            small = Text(EditorStyles.label, p.TextMuted, SRMetrics.FontSmall, FontStyle.Normal);
            mini = Text(EditorStyles.label, p.TextMuted, SRMetrics.FontMini, FontStyle.Bold);
            mini.alignment = TextAnchor.MiddleCenter;
            hero = Text(EditorStyles.label, p.TextStrong, SRMetrics.FontHero, FontStyle.Bold);
            hero.alignment = TextAnchor.MiddleCenter;
            hero.wordWrap = true;
            center = Text(EditorStyles.label, p.TextMuted, SRMetrics.FontBody, FontStyle.Normal);
            center.alignment = TextAnchor.MiddleCenter;
            center.wordWrap = true;
            wrap = Text(EditorStyles.label, p.Text, SRMetrics.FontBody, FontStyle.Normal);
            wrap.wordWrap = true;
            // Layout-only copies: GUILayout sizes the help box from the width it actually assigns.
            helpLayout = new GUIStyle(wrap) { padding = new RectOffset(32, 8, 6, 6) };
            helpActionLayout = new GUIStyle(wrap) { padding = new RectOffset(32, 8, 6, 6 + (int)SRMetrics.ButtonMini + 4) };

            buttonText = Text(EditorStyles.label, p.TextStrong, SRMetrics.FontBody, FontStyle.Normal);
            buttonText.alignment = TextAnchor.MiddleCenter;
            buttonText.padding = new RectOffset(8, 8, 0, 0);
            buttonText.imagePosition = ImagePosition.ImageLeft;

            fieldText = Bare(EditorStyles.textField, p.Text);
            fieldText.fontSize = SRMetrics.FontBody;
            fieldText.padding = new RectOffset(4, 4, 1, 1);
            popupText = Bare(EditorStyles.popup, p.TextStrong);
            popupText.padding = new RectOffset(6, 18, 1, 1);
        }

        private static GUIStyle Text(GUIStyle source, Color color, int size, FontStyle fontStyle)
        {
            var s = new GUIStyle(source) { fontSize = size, fontStyle = fontStyle, richText = true };
            SetTextColor(s, color);
            s.margin = new RectOffset(0, 0, 0, 0);
            s.padding = new RectOffset(0, 0, 0, 0);
            return s;
        }

        /// <summary>Copy of a Unity style with every background removed; we paint the face ourselves.</summary>
        private static GUIStyle Bare(GUIStyle source, Color color)
        {
            var s = new GUIStyle(source);
            foreach (GUIStyleState st in States(s))
            {
                st.background = null;
                st.scaledBackgrounds = new Texture2D[0];
                st.textColor = color;
            }
            s.border = new RectOffset(0, 0, 0, 0);
            s.margin = new RectOffset(0, 0, 0, 0);
            return s;
        }

        internal static GUIStyleState[] States(GUIStyle s)
        {
            return new[] { s.normal, s.hover, s.active, s.focused, s.onNormal, s.onHover, s.onActive, s.onFocused };
        }

        internal static void SetTextColor(GUIStyle s, Color c)
        {
            foreach (GUIStyleState st in States(s)) st.textColor = c;
        }
    }

    /// <summary>
    /// Colors Unity's own prefix label (EditorStyles.label) for the duration of one field, so the
    /// label keeps Unity's drag-to-change and prefab-override behavior but uses our palette.
    /// Focused label = focus color (Unity uses blue). Restored on Dispose.
    /// </summary>
    public struct SRLabelScope : IDisposable
    {
        private readonly Color[] saved;
        private readonly bool savedRich;
        private readonly int savedFontSize;

        private SRLabelScope(Color[] saved, bool savedRich, int savedFontSize)
        {
            this.saved = saved;
            this.savedRich = savedRich;
            this.savedFontSize = savedFontSize;
        }

        public static SRLabelScope Begin()
        {
            GUIStyle s = EditorStyles.label;
            GUIStyleState[] states = SRStyles.States(s);
            var saved = new Color[states.Length];
            for (int i = 0; i < states.Length; i++) saved[i] = states[i].textColor;
            var scope = new SRLabelScope(saved, s.richText, s.fontSize);
            SRPalette p = SRTheme.P;
            // disabled: Unity halves the label; pre-compensated for the window face (card/section
            // land within 8/255 of text-disabled)
            Color ink = GUI.enabled ? p.Text : SRTheme.DisabledInk(p.SurfaceWindow);
            s.normal.textColor = ink;
            s.hover.textColor = ink;
            s.active.textColor = p.Focus;
            s.focused.textColor = p.Focus;
            s.onNormal.textColor = p.Focus;
            s.onHover.textColor = p.Focus;
            s.onActive.textColor = p.Focus;
            s.onFocused.textColor = p.Focus;
            s.richText = true;
            s.fontSize = SRMetrics.FontBody;
            return scope;
        }

        public void Dispose()
        {
            if (saved == null) return;
            GUIStyle s = EditorStyles.label;
            GUIStyleState[] states = SRStyles.States(s);
            for (int i = 0; i < states.Length && i < saved.Length; i++) states[i].textColor = saved[i];
            s.fontSize = savedFontSize;
            s.richText = savedRich;
        }
    }

    /// <summary>Redrawn IMGUI controls in the StudioRaming theme.</summary>
    public static class SRGui
    {
        private static readonly int ButtonHash = "SRGui.Button".GetHashCode();
        private static readonly int DragHash = "SRGui.LabelDrag".GetHashCode();
        private static readonly int SectionHash = "SRGui.Section".GetHashCode();
        private static int fieldSerial;

        // ------------------------------------------------------------ labels

        /// <summary>
        /// "번역 (Original)" label: the original term is muted. Pass the same string twice (or null
        /// original) in the English UI so no parentheses are shown. Tooltip is required by the guide.
        /// </summary>
        public static GUIContent Label(string translated, string original, string tooltip)
        {
            if (string.IsNullOrEmpty(original) || original == translated) return new GUIContent(translated, tooltip);
            return new GUIContent(translated + " <color=" + SRTheme.Hex(SRTheme.P.TextMuted) + ">(" + original + ")</color>", tooltip);
        }

        /// <summary>Same, decided by language: English shows the original only; ko/ja add it muted.</summary>
        public static GUIContent Label(SRLang lang, string translated, string original, string tooltip)
        {
            return lang == SRLang.English ? new GUIContent(original, tooltip) : Label(translated, original, tooltip);
        }

        /// <summary>A label line without a field (muted original kept). Rich text is on in SRStyles.</summary>
        public static void LabelRow(GUIContent label)
        {
            Rect row = EditorGUILayout.GetControlRect(false, SRMetrics.Line);
            Ink(row, label, SRStyles.Body);
        }

        // ------------------------------------------------------------ disabled text

        private static readonly GUIContent InkContent = new GUIContent();
        private static readonly Regex ColorTag = new Regex("</?color[^>]*>", RegexOptions.IgnoreCase);

        /// <summary>
        /// GUI.Label for SRGui text on a neutral face. Enabled: the style's colour. Disabled: text-disabled
        /// drawn once, with Unity's half-alpha blend suspended for this label only (labels take no input),
        /// and rich-text colour tags dropped so a muted "(Original)" cannot outshine the disabled text.
        /// Not used for the header band (window chrome, never disabled; text-disabled on surface-app is
        /// 2.59:1 in light) or for text on a coloured fill (header mark, help box icon, badges: a brand
        /// badge would drop to 2.5:1 on brand-tint): those keep Unity's blend.
        /// </summary>
        public static void Ink(Rect r, GUIContent content, GUIStyle style)
        {
            if (GUI.enabled)
            {
                GUI.Label(r, content, style);
                return;
            }
            GUIContent c = content;
            if (content != null && content.text != null && content.text.IndexOf("<color", StringComparison.OrdinalIgnoreCase) >= 0)
                c = new GUIContent(ColorTag.Replace(content.text, string.Empty), content.image, content.tooltip);
            Color oldNormal = style.normal.textColor;
            Color oldHover = style.hover.textColor;
            style.normal.textColor = SRTheme.P.TextDisabled;
            style.hover.textColor = SRTheme.P.TextDisabled;
            GUI.enabled = true;
            try { GUI.Label(r, c, style); }
            finally
            {
                GUI.enabled = false;
                style.normal.textColor = oldNormal;
                style.hover.textColor = oldHover;
            }
        }

        public static void Ink(Rect r, string text, GUIStyle style)
        {
            if (GUI.enabled)
            {
                GUI.Label(r, text, style);
                return;
            }
            InkContent.text = text;
            Ink(r, InkContent, style);
        }

        // ------------------------------------------------------------ window chrome

        /// <summary>Paints the whole window with surface-window. Call first in OnGUI.</summary>
        public static void WindowBackground(EditorWindow window)
        {
            if (window == null) return;
            SRTheme.Fill(new Rect(0f, 0f, window.position.width, window.position.height), SRTheme.P.SurfaceWindow);
        }

        /// <summary>Header band (44px + 2px product line). Returns the right-side tool area.</summary>
        public static Rect Header(SRProduct product, string markLetter, string title, string subtitle)
        {
            Rect r = GUILayoutUtility.GetRect(0f, SRMetrics.Header + SRMetrics.AccentLine, GUILayout.ExpandWidth(true));
            SRPalette p = SRTheme.P;
            Color accent = SRTheme.Product(product);
            SRTheme.Fill(new Rect(r.x, r.y, r.width, SRMetrics.Header), p.SurfaceApp);
            SRTheme.Fill(new Rect(r.x, r.y + SRMetrics.Header, r.width, SRMetrics.AccentLine), accent);

            var mark = new Rect(r.x + SRMetrics.Space12, r.y + (SRMetrics.Header - 22f) * 0.5f, 22f, 22f);
            SRTheme.Fill(mark, accent, SRMetrics.RadiusButton);
            GUIStyle markStyle = SRStyles.Strong;
            Color old = markStyle.normal.textColor;
            markStyle.normal.textColor = p.SurfaceSunken;
            TextAnchor oldAnchor = markStyle.alignment;
            markStyle.alignment = TextAnchor.MiddleCenter;
            GUI.Label(mark, markLetter, markStyle);
            markStyle.alignment = oldAnchor;
            markStyle.normal.textColor = old;

            float textX = mark.xMax + SRMetrics.Space8;
            float toolsWidth = Mathf.Min(140f, r.width * 0.35f);
            float textW = Mathf.Max(0f, r.xMax - SRMetrics.Space12 - toolsWidth - textX);
            bool showSub = !string.IsNullOrEmpty(subtitle) && r.width >= SRMetrics.InspectorMin + 40f;
            float titleY = showSub ? r.y + 6f : r.y + (SRMetrics.Header - 18f) * 0.5f;
            GUI.Label(new Rect(textX, titleY, textW, 18f), Fit(title, SRStyles.Title, textW), SRStyles.Title);
            if (showSub) GUI.Label(new Rect(textX, titleY + 19f, textW, 14f), Fit(subtitle, SRStyles.Small, textW), SRStyles.Small);

            return new Rect(r.xMax - SRMetrics.Space12 - toolsWidth, r.y + (SRMetrics.Header - SRMetrics.Line) * 0.5f, toolsWidth, SRMetrics.Line);
        }

        /// <summary>Tab row (24px). Labels are left-aligned and hug their text (Unity rule).</summary>
        public static int Tabs(int current, GUIContent[] tabs, int[] badges = null)
        {
            Rect r = GUILayoutUtility.GetRect(0f, SRMetrics.Tab, GUILayout.ExpandWidth(true));
            SRPalette p = SRTheme.P;
            SRTheme.Fill(r, p.SurfaceSection);
            SRTheme.Fill(new Rect(r.x, r.yMax - 1f, r.width, 1f), p.LineSubtle);
            float x = r.x;
            for (int i = 0; i < tabs.Length; i++)
            {
                bool on = i == current;
                GUIStyle style = on ? SRStyles.Strong : SRStyles.Body;
                float w = style.CalcSize(tabs[i]).x + 24f;
                string badge = badges != null && i < badges.Length && badges[i] > 0 ? badges[i].ToString() : null;
                if (badge != null) w += BadgeWidth(badge) + 4f;
                var tr = new Rect(x, r.y, Mathf.Min(w, r.xMax - x), r.height - 1f);
                if (tr.width <= 0f) break;
                bool hover = GUI.enabled && tr.Contains(Event.current.mousePosition);
                // disabled: no tint (text-disabled on tint is 2.5:1 in dark); the underline keeps the selection
                if (on && GUI.enabled) SRTheme.Fill(tr, p.BrandTint);
                else if (hover) SRTheme.Fill(tr, p.SurfaceHover);
                if (on) SRTheme.Fill(new Rect(tr.x, tr.yMax - 1f, tr.width, SRMetrics.AccentLine), GUI.enabled ? p.BrandLine : p.TextDisabled);
                Color oldColor = style.normal.textColor;
                style.normal.textColor = on ? p.TextStrong : (hover ? p.Text : p.TextMuted);
                Ink(new Rect(tr.x + 12f, tr.y, tr.width - 12f, tr.height), tabs[i], style);
                style.normal.textColor = oldColor;
                if (badge != null) Badge(new Rect(tr.xMax - 12f - BadgeWidth(badge), tr.y + 3f, BadgeWidth(badge), 18f), badge, SRTone.Warning);
                if (GUI.Button(tr, GUIContent.none, GUIStyle.none)) current = i;
                x += tr.width;
            }
            return current;
        }

        /// <summary>1-2-3 step guide. Steps before current are done; returns a clicked done step or current.</summary>
        public static int StepGuide(string[] steps, int current)
        {
            Rect r = GUILayoutUtility.GetRect(0f, 36f, GUILayout.ExpandWidth(true));
            SRPalette p = SRTheme.P;
            SRTheme.Box(r, p.SurfaceCard, p.LineSubtle, SRMetrics.RadiusCard);
            float[] widths = new float[steps.Length];
            float total = 0f;
            for (int i = 0; i < steps.Length; i++)
            {
                GUIStyle s = i == current ? SRStyles.Strong : SRStyles.Small;
                widths[i] = 18f + 6f + s.CalcSize(new GUIContent(steps[i])).x;
                total += widths[i];
            }
            float gap = steps.Length > 1 ? Mathf.Max(8f, (r.width - 16f - total) / (steps.Length - 1)) : 0f;
            float x = r.x + 8f;
            float cy = r.y + r.height * 0.5f;
            int result = current;
            for (int i = 0; i < steps.Length; i++)
            {
                var dot = new Rect(x, cy - 9f, 18f, 18f);
                bool done = i < current;
                bool now = i == current;
                if (done) SRTheme.Box(dot, p.Brand, p.BrandBorder, 9f);
                else if (now) { SRTheme.Fill(dot, p.SurfaceWindow, 9f); SRTheme.Stroke(dot, p.BrandLine, 9f, 2f); }
                else SRTheme.Box(dot, p.SurfaceSunken, p.LineStrong, 9f);
                if (done)
                {
                    Glyph(new Rect(dot.x + 3f, dot.y + 3f, 12f, 12f), SRGlyph.Check, p.TextOnBrand, 1.8f);
                }
                else
                {
                    GUIStyle num = SRStyles.Mini;
                    Color old = num.normal.textColor;
                    num.normal.textColor = now ? p.BrandText : p.TextMuted;
                    Ink(dot, (i + 1).ToString(), num);
                    num.normal.textColor = old;
                }
                GUIStyle ls = now ? SRStyles.Strong : SRStyles.Small;
                var lr = new Rect(dot.xMax + 6f, r.y, widths[i] - 24f, r.height);
                Ink(lr, steps[i], ls);
                if (done)
                {
                    EditorGUIUtility.AddCursorRect(new Rect(dot.x, r.y, widths[i], r.height), MouseCursor.Link);
                    if (GUI.Button(new Rect(dot.x, r.y, widths[i], r.height), GUIContent.none, GUIStyle.none)) result = i;
                }
                x += widths[i];
                if (i < steps.Length - 1)
                {
                    SRTheme.Fill(new Rect(x + 4f, cy, gap - 8f, 1f), p.LineStrong);
                    x += gap;
                }
            }
            return result;
        }

        /// <summary>
        /// Foldout section header (22px). The whole row toggles (Unity inspector rule). State persists
        /// per user under prefKey. resetClicked reports the right-side "기본값" link.
        /// </summary>
        public static bool Section(string prefKey, GUIContent label, bool defaultOpen, string resetLabel, out bool resetClicked)
        {
            bool open = EditorPrefs.GetBool(prefKey, defaultOpen);
            bool now = SectionHeader(open, label, resetLabel, out resetClicked);
            if (now != open) EditorPrefs.SetBool(prefKey, now);
            return now;
        }

        /// <summary>Section whose open state the caller keeps (not saved between sessions).</summary>
        public static bool Section(ref bool open, GUIContent label, string resetLabel, out bool resetClicked)
        {
            open = SectionHeader(open, label, resetLabel, out resetClicked);
            return open;
        }

        private static bool SectionHeader(bool open, GUIContent label, string resetLabel, out bool resetClicked)
        {
            resetClicked = false;
            Rect r = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            r.y += 2f;
            SRPalette p = SRTheme.P;
            SRTheme.Fill(r, p.SurfaceSection);
            SRTheme.Fill(new Rect(r.x, r.y, r.width, 1f), p.LineSubtle);
            SRTheme.Fill(new Rect(r.x, r.yMax - 1f, r.width, 1f), p.LineSubtle);

            Rect resetRect = Rect.zero;
            if (!string.IsNullOrEmpty(resetLabel))
            {
                float rw = SRStyles.Small.CalcSize(new GUIContent(resetLabel)).x + 8f;
                resetRect = new Rect(r.xMax - rw - 4f, r.y, rw, r.height);
                Ink(resetRect, resetLabel, SRStyles.Small);
                if (GUI.Button(resetRect, GUIContent.none, GUIStyle.none)) resetClicked = true;
                if (GUI.enabled) EditorGUIUtility.AddCursorRect(resetRect, MouseCursor.Link);
            }

            GUIStyle ls = open ? SRStyles.Section : SRStyles.Body;
            Glyph(new Rect(r.x + 6f, r.y + 5f, 12f, 12f), open ? SRGlyph.ChevronDown : SRGlyph.ChevronRight, GUI.enabled ? p.TextMuted : p.TextDisabled);
            var titleRect = new Rect(r.x + 20f, r.y + 2f, r.width - 24f - resetRect.width, 18f);
            Ink(titleRect, FitContent(label, ls, titleRect.width), ls);

            GUIUtility.GetControlID(SectionHash, FocusType.Passive, r);
            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition) && !resetRect.Contains(e.mousePosition))
            {
                open = !open;
                GUIUtility.keyboardControl = 0;
                e.Use();
            }
            GUILayout.Space(4f);
            return open;
        }

        public static void BeginCard()
        {
            Rect r = EditorGUILayout.BeginVertical();
            SRTheme.Box(r, SRTheme.P.SurfaceCard, SRTheme.P.LineSubtle, SRMetrics.RadiusCard);
            GUILayout.Space(SRMetrics.Space8);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(SRMetrics.Space8);
            EditorGUILayout.BeginVertical();
        }

        public static void EndCard()
        {
            EditorGUILayout.EndVertical();
            GUILayout.Space(SRMetrics.Space8);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(SRMetrics.Space8);
            EditorGUILayout.EndVertical();
        }

        // ------------------------------------------------------------ fields (label drag kept by Unity)

        private static Rect FieldPart(Rect row)
        {
            float x = row.x + EditorGUIUtility.labelWidth + 2f;
            return new Rect(x, row.y, Mathf.Max(0f, row.xMax - x), row.height);
        }

        private static string NextFieldName()
        {
            fieldSerial = (fieldSerial + 1) & 0xFFFF;
            return "SRGui.Field." + fieldSerial;
        }

        /// <summary>Field face (sunken fill + control border). Drawn before the Unity field.</summary>
        private static void FieldFace(Rect field)
        {
            SRPalette p = SRTheme.P;
            SRTheme.Box(field, p.SurfaceSunken, p.LineControl, SRMetrics.RadiusControl);
            SetFieldInk();
        }

        /// <summary>FieldText colour for the next Unity-drawn field value (shared style, so set per field).</summary>
        private static void SetFieldInk()
        {
            SRPalette p = SRTheme.P;
            SRStyles.FieldText.normal.textColor = GUI.enabled ? p.Text : SRTheme.DisabledInk(p.SurfaceSunken);
        }

        /// <summary>
        /// Focus ring, drawn AFTER the Unity field: control names are registered per event, so the
        /// focused name only matches once this field has registered its name in the current event.
        /// </summary>
        private static void FieldFocus(Rect field, string controlName)
        {
            if (!GUI.enabled || GUI.GetNameOfFocusedControl() != controlName) return;
            SRPalette p = SRTheme.P;
            SRTheme.Stroke(field, p.Focus, SRMetrics.RadiusControl, 1f);
            SRTheme.Stroke(new Rect(field.x + 1f, field.y + 1f, field.width - 2f, field.height - 2f), p.Focus, SRMetrics.RadiusControl, 1f);
        }

        public static float FloatField(GUIContent label, float value, params GUILayoutOption[] options)
        {
            Rect row = EditorGUILayout.GetControlRect(true, SRMetrics.Line, options);
            Rect field = FieldPart(row);
            string name = NextFieldName();
            FieldFace(field);
            float result;
            using (SRLabelScope.Begin())
            {
                GUI.SetNextControlName(name);
                result = EditorGUI.FloatField(row, label, value, SRStyles.FieldText);
            }
            FieldFocus(field, name);
            return result;
        }

        /// <summary>A value-only field for compact rows, with the same face and focus ring.</summary>
        public static float FloatField(Rect field, float value)
        {
            string name = NextFieldName();
            FieldFace(field);
            GUI.SetNextControlName(name);
            float result = EditorGUI.FloatField(field, value, SRStyles.FieldText);
            FieldFocus(field, name);
            return result;
        }

        /// <summary>A section heading, larger than body text, with a shared separator.</summary>
        public static Rect SectionLabel(GUIContent content)
        {
            GUILayout.Space(SRMetrics.Space6);
            Rect row = EditorGUILayout.GetControlRect(false, SRMetrics.FontSection + SRMetrics.Space8);
            SectionLabel(row, content);
            return row;
        }

        public static void SectionLabel(Rect row, GUIContent content)
        {
            SRTheme.Fill(new Rect(row.x, row.yMax - 1f, row.width, 1f), SRTheme.P.LineStrong);
            Rect text = new Rect(row.x, row.y, row.width, row.height - 3f);
            Ink(text, FitContent(content, SRStyles.Section, text.width), SRStyles.Section);
        }

        /// <summary>Shared track-only slider for compact material rows with their own number field.</summary>
        public static float SliderTrack(Rect row, float value, float minimum, float maximum)
        {
            SRPalette p = SRTheme.P;
            float inset = Mathf.Min(7f, row.width * 0.25f);
            Rect track = new Rect(row.x + inset, row.y, Mathf.Max(0f, row.width - inset * 2f), row.height);
            float next = GUI.HorizontalSlider(row, value, minimum, maximum, GUIStyle.none, ThumbHit());
            float t = Mathf.InverseLerp(minimum, maximum, next);
            Rect groove = new Rect(track.x, track.center.y - 2f, track.width, 4f);
            SRTheme.Fill(groove, p.LineControl, 2f);
            if (!EditorGUI.showMixedValue)
            {
                SRTheme.Fill(new Rect(groove.x, groove.y, groove.width * t, groove.height),
                    GUI.enabled ? p.BrandLine : p.TextDisabled, 2f);
                Rect thumb = new Rect(track.x + track.width * t - 7f, track.center.y - 7f, 14f, 14f);
                SRTheme.Box(thumb, GUI.enabled ? p.Brand : p.TextDisabled, p.BrandBorder, 7f);
            }
            return next;
        }

        public static int IntField(GUIContent label, int value, params GUILayoutOption[] options)
        {
            Rect row = EditorGUILayout.GetControlRect(true, SRMetrics.Line, options);
            Rect field = FieldPart(row);
            string name = NextFieldName();
            FieldFace(field);
            int result;
            using (SRLabelScope.Begin())
            {
                GUI.SetNextControlName(name);
                result = EditorGUI.IntField(row, label, value, SRStyles.FieldText);
            }
            FieldFocus(field, name);
            return result;
        }

        public static string TextField(GUIContent label, string value, params GUILayoutOption[] options)
        {
            return TextField(null, label, value, options);
        }

        /// <summary>Text field with a caller-chosen control name (for GUI.FocusControl / EditorGUI.FocusTextInControl).
        /// Null name = generated.</summary>
        public static string TextField(string controlName, GUIContent label, string value, params GUILayoutOption[] options)
        {
            Rect row = EditorGUILayout.GetControlRect(true, SRMetrics.Line, options);
            Rect field = label == null || label == GUIContent.none ? row : FieldPart(row);
            string name = string.IsNullOrEmpty(controlName) ? NextFieldName() : controlName;
            FieldFace(field);
            string result;
            using (SRLabelScope.Begin())
            {
                GUI.SetNextControlName(name);
                result = label == null || label == GUIContent.none
                    ? EditorGUI.TextField(row, value, SRStyles.FieldText)
                    : EditorGUI.TextField(row, label, value, SRStyles.FieldText);
            }
            FieldFocus(field, name);
            return result;
        }

        /// <summary>SerializedProperty float: prefab override bold, right-click revert, Undo.</summary>
        public static void PropertyFloat(SerializedProperty prop, GUIContent label)
        {
            Rect row = EditorGUILayout.GetControlRect(true, SRMetrics.Line);
            Rect field = FieldPart(row);
            string name = NextFieldName();
            FieldFace(field);
            using (SRLabelScope.Begin())
            {
                label = EditorGUI.BeginProperty(row, label, prop);
                EditorGUI.BeginChangeCheck();
                GUI.SetNextControlName(name);
                float v = EditorGUI.FloatField(row, label, prop.floatValue, SRStyles.FieldText);
                if (EditorGUI.EndChangeCheck()) prop.floatValue = v;
                EditorGUI.EndProperty();
            }
            FieldFocus(field, name);
        }

        public static int Popup(GUIContent label, int index, GUIContent[] options)
        {
            return Popup(EditorGUILayout.GetControlRect(true, SRMetrics.Line), label, index, options);
        }

        /// <summary>Dropdown in a given row rect. A null or empty label uses the whole row for the field.</summary>
        public static int Popup(Rect row, GUIContent label, int index, GUIContent[] options)
        {
            bool labelled = label != null && label != GUIContent.none;
            Rect field = labelled ? FieldPart(row) : row;
            if (labelled)
            {
                Ink(
                    new Rect(
                        row.x,
                        row.y,
                        Mathf.Max(0f, field.x - row.x - SRMetrics.Spacing),
                        row.height),
                    label,
                    SRStyles.Body);
            }
            PopupValue.text = EditorGUI.showMixedValue
                ? MixedDash
                : options != null && index >= 0 && index < options.Length && options[index] != null
                    ? options[index].text
                    : string.Empty;
            PopupValue.tooltip = label != null ? label.tooltip : null;
            // Like Unity's popup, a field without a label tooltip shows the
            // chosen option's own tooltip.
            if (string.IsNullOrEmpty(PopupValue.tooltip) &&
                options != null && index >= 0 && index < options.Length &&
                options[index] != null)
            {
                PopupValue.tooltip = options[index].tooltip;
            }
            // Unity keeps the interaction (click, keyboard, the menu itself) and paints its
            // own popup widget; DropdownFace runs after it, so the face, the value and the
            // arrow on screen are DropdownFace's - the same painting as a menu-style
            // dropdown button (MingInspectorRowControls.DropdownButton). Painting first let
            // Unity's widget cover the value, which is what made popups look unlike them.
            int result = EditorGUI.Popup(field, index, options, PopupGhost());
            DropdownFace(field, PopupValue);
            return result;
        }

        private static readonly GUIContent PopupValue = new GUIContent();
        private const string MixedDash = "\u2014";
        private static GUIStyle popupGhost;

        private static GUIContent[] stringOptions = new GUIContent[0];

        /// <summary>String list as content, reusing one buffer per length (Unity's Popup takes GUIContent[]).</summary>
        private static GUIContent[] AsContent(string[] options)
        {
            if (options == null)
            {
                return null;
            }
            if (stringOptions.Length != options.Length)
            {
                var grown = new GUIContent[options.Length];
                for (int i = 0; i < grown.Length; i++)
                {
                    grown[i] = new GUIContent();
                }
                stringOptions = grown;
            }
            for (int i = 0; i < options.Length; i++)
            {
                stringOptions[i].text = options[i];
            }
            return stringOptions;
        }

        /// <summary>Unity's string-list popup signature, drawn in the shared dropdown face.</summary>
        public static int Popup(GUIContent label, int index, string[] options)
        {
            return Popup(label, index, AsContent(options));
        }

        /// <summary>A string-list popup in a given row rect (label + field), shared face.</summary>
        public static int Popup(Rect row, GUIContent label, int index, string[] options)
        {
            return Popup(row, label, index, AsContent(options));
        }

        /// <summary>Unity's label-less popup: the whole row is the field, no label width reserved.</summary>
        public static int Popup(int index, string[] options)
        {
            return Popup(
                EditorGUILayout.GetControlRect(false, SRMetrics.Line),
                GUIContent.none,
                index,
                AsContent(options));
        }

        /// <summary>A label-less popup with layout options (a fixed-width slot column).</summary>
        public static int Popup(int index, string[] options, params GUILayoutOption[] layout)
        {
            return Popup(index, AsContent(options), layout);
        }

        /// <summary>A label-less content popup with layout options.</summary>
        public static int Popup(int index, GUIContent[] options, params GUILayoutOption[] layout)
        {
            return Popup(
                EditorGUILayout.GetControlRect(false, SRMetrics.Line, layout),
                GUIContent.none,
                index,
                options);
        }

        /// <summary>Unity's string-label popup, drawn in the shared dropdown face.</summary>
        public static int Popup(string label, int index, string[] options)
        {
            TextLabel.text = label ?? string.Empty;
            TextLabel.tooltip = null;
            return Popup(TextLabel, index, options);
        }

        /// <summary>
        /// Unity's IntPopup (display names mapped to values), drawn in the shared face.
        /// A value missing from <paramref name="values"/> shows empty and is kept until picked.
        /// </summary>
        public static int IntPopup(GUIContent label, int selectedValue, GUIContent[] displayed, int[] values)
        {
            return IntPopup(EditorGUILayout.GetControlRect(true, SRMetrics.Line), label, selectedValue, displayed, values);
        }

        public static int IntPopup(Rect row, GUIContent label, int selectedValue, GUIContent[] displayed, int[] values)
        {
            int index = values != null ? Array.IndexOf(values, selectedValue) : -1;
            int picked = Popup(row, label, index, displayed);
            return picked >= 0 && values != null && picked < values.Length && picked != index
                ? values[picked]
                : selectedValue;
        }

        /// <summary>Unity's enum popup, drawn in the shared dropdown face.</summary>
        public static Enum EnumPopup(GUIContent label, Enum value)
        {
            return EnumPopup(EditorGUILayout.GetControlRect(true, SRMetrics.Line), label, value);
        }

        private static readonly GUIContent TextLabel = new GUIContent();

        /// <summary>Enum popup whose label is a plain string (no GUIContent allocation per repaint).</summary>
        public static Enum EnumPopup(string label, Enum value)
        {
            TextLabel.text = label ?? string.Empty;
            TextLabel.tooltip = null;
            return EnumPopup(TextLabel, value);
        }

        public static Enum EnumPopup(Rect row, GUIContent label, Enum value)
        {
            bool labelled = label != null && label != GUIContent.none;
            Rect field = labelled ? FieldPart(row) : row;
            if (labelled)
            {
                Ink(
                    new Rect(
                        row.x,
                        row.y,
                        Mathf.Max(0f, field.x - row.x - SRMetrics.Spacing),
                        row.height),
                    label,
                    SRStyles.Body);
            }
            PopupValue.text = value == null
                ? string.Empty
                : ObjectNames.NicifyVariableName(value.ToString());
            PopupValue.tooltip = label != null ? label.tooltip : null;
            Enum result = EditorGUI.EnumPopup(field, value, PopupGhost());
            DropdownFace(field, PopupValue);
            return result;
        }

        private static GUIStyle PopupGhost()
        {
            if (popupGhost == null)
            {
                popupGhost = new GUIStyle(EditorStyles.popup);
                foreach (GUIStyleState state in SRStyles.States(popupGhost))
                {
                    state.background = null;
                    state.scaledBackgrounds = new Texture2D[0];
                    state.textColor = new Color(0f, 0f, 0f, 0f);
                }
                popupGhost.border = new RectOffset(0, 0, 0, 0);
                popupGhost.margin = new RectOffset(0, 0, 0, 0);
                popupGhost.padding = new RectOffset(0, 0, 0, 0);
            }
            return popupGhost;
        }

        /// <summary>
        /// The one dropdown face in the product: control fill (hover), control border, body ink at
        /// the shared text inset and the SR chevron at the shared arrow inset. A menu-style dropdown
        /// (<see cref="MingInspectorRowControls.DropdownButton"/>) and a real popup
        /// (<see cref="Popup(Rect, GUIContent, int, GUIContent[])"/>) both paint through here, so the
        /// two can never drift apart again.
        /// </summary>
        public static void DropdownFace(Rect field, GUIContent content)
        {
            SRPalette p = SRTheme.P;
            bool hover = GUI.enabled && field.Contains(Event.current.mousePosition);
            if (GUI.enabled)
            {
                SRTheme.Box(field, hover ? p.ControlHover : p.Control, p.LineControl, SRMetrics.RadiusControl);
            }
            else
            {
                SRTheme.Box(field, p.SurfaceCard, p.LineSubtle, SRMetrics.RadiusControl);
            }
            if (content != null)
            {
                Rect text = new Rect(
                    field.x + SRMetrics.RowDropdownTextInset,
                    field.y,
                    Mathf.Max(0f, field.width - SRMetrics.RowDropdownArrowInset - SRMetrics.RowLinkPad - SRMetrics.RowFieldPadding),
                    field.height);
                Ink(text, FitContent(content, SRStyles.Body, text.width), SRStyles.Body);
            }
            Glyph(
                new Rect(
                    field.xMax - SRMetrics.RowDropdownArrowInset,
                    field.y + (field.height - SRMetrics.RowArrowSize) * 0.5f,
                    SRMetrics.RowArrowSize,
                    SRMetrics.RowArrowSize),
                SRGlyph.ChevronDown,
                GUI.enabled ? p.TextMuted : p.TextDisabled);
        }

        /// <summary>Checkbox. Clicking the label toggles it (Unity rule). Mixed values show a dash.</summary>
        public static bool Toggle(GUIContent label, bool value)
        {
            return Toggle(EditorGUILayout.GetControlRect(true, SRMetrics.Line), label, value);
        }

        public static bool Toggle(Rect row, GUIContent label, bool value)
        {
            bool labelled = label != null && !string.IsNullOrEmpty(label.text);
            Rect field = labelled ? FieldPart(row) : row;
            var box = new Rect(field.x, field.y + 2f, 14f, 14f);
            SRPalette p = SRTheme.P;
            bool mixed = EditorGUI.showMixedValue;
            bool result;
            using (SRLabelScope.Begin()) result = EditorGUI.Toggle(row, label, value, GUIStyle.none);
            CheckFace(box, result, mixed);
            return result;
        }

        private static void CheckFace(Rect box, bool on, bool mixed)
        {
            SRPalette p = SRTheme.P;
            if (on && !mixed)
            {
                SRTheme.Box(box, GUI.enabled ? p.Brand : p.TextDisabled, p.BrandBorder, SRMetrics.RadiusControl);
                Glyph(new Rect(box.x + 1f, box.y + 1f, 12f, 12f), SRGlyph.Check, p.TextOnBrand, 1.8f);
            }
            else
            {
                SRTheme.Box(box, p.SurfaceSunken, GUI.enabled ? p.LineStrong : p.LineControl, SRMetrics.RadiusControl);
                if (mixed) SRTheme.Fill(new Rect(box.x + 3f, box.y + 6f, 8f, 2f), GUI.enabled ? p.Text : p.TextDisabled);
            }
        }

        /// <summary>Checkbox first, label after it; the whole row toggles (Unity ToggleLeft).</summary>
        public static bool ToggleLeft(GUIContent label, bool value)
        {
            Rect row = EditorGUILayout.GetControlRect(false, SRMetrics.Line);
            return ToggleLeft(row, label, value);
        }

        public static bool ToggleLeft(Rect row, GUIContent label, bool value)
        {
            var box = new Rect(row.x, row.y + 2f, 14f, 14f);
            bool mixed = EditorGUI.showMixedValue;
            bool result = GUI.Toggle(row, value, GUIContent.none, GUIStyle.none);
            CheckFace(box, result, mixed);
            Ink(new Rect(box.xMax + 6f, row.y, row.width - 20f, row.height), label, SRStyles.Body);
            return result;
        }

        public static void PropertyToggle(SerializedProperty prop, GUIContent label)
        {
            Rect row = EditorGUILayout.GetControlRect(true, SRMetrics.Line);
            label = EditorGUI.BeginProperty(row, label, prop);
            EditorGUI.BeginChangeCheck();
            bool v = Toggle(row, label, prop.boolValue);
            if (EditorGUI.EndChangeCheck()) prop.boolValue = v;
            EditorGUI.EndProperty();
        }

        /// <summary>Label + slider + number field (Unity layout). Label drag and groove click are kept.</summary>
        public static float Slider(GUIContent label, float value, float min, float max)
        {
            return Slider(EditorGUILayout.GetControlRect(true, SRMetrics.Line), label, value, min, max);
        }

        public static float Slider(Rect row, GUIContent label, float value, float min, float max)
        {
            SRPalette p = SRTheme.P;
            bool labelled = label != null && !string.IsNullOrEmpty(label.text);
            var labelRect = new Rect(row.x, row.y, labelled ? EditorGUIUtility.labelWidth : 0f, row.height);
            Rect field = SliderFieldRect(row, label);
            float numW = Mathf.Min(field.width, Mathf.Clamp(field.width * 0.4f, 45f, 52f));
            var num = new Rect(field.xMax - numW, field.y, numW, field.height);
            var track = new Rect(field.x + 7f, field.y, Mathf.Max(0f, field.width - numW - 6f - 14f), field.height);

            if (labelled)
            {
                using (SRLabelScope.Begin()) EditorGUI.LabelField(labelRect, label);
                value = LabelDrag(labelRect, value, min, max, track.width);
            }

            value = SliderTrack(new Rect(track.x - 7f, track.y, track.width + 14f, track.height), value, min, max);

            string name = NextFieldName();
            FieldFace(num);
            GUI.SetNextControlName(name);
            value = Mathf.Clamp(EditorGUI.FloatField(num, value, SRStyles.FieldText), min, max);
            FieldFocus(num, name);
            return value;
        }

        internal static Rect SliderFieldRect(Rect row, GUIContent label)
        {
            // labelWidth=0 asks Unity to restore its default, not to reserve
            // zero pixels. A value-only cell must use its supplied rectangle.
            return label != null && !string.IsNullOrEmpty(label.text) ? FieldPart(row) : row;
        }

        public static void PropertySlider(SerializedProperty prop, GUIContent label, float min, float max)
        {
            Rect row = EditorGUILayout.GetControlRect(true, SRMetrics.Line);
            label = EditorGUI.BeginProperty(row, label, prop);
            EditorGUI.BeginChangeCheck();
            float v = Slider(row, label, prop.floatValue, min, max);
            if (EditorGUI.EndChangeCheck()) prop.floatValue = v;
            EditorGUI.EndProperty();
        }

        private static GUIStyle thumbHit;

        private static GUIStyle ThumbHit()
        {
            if (thumbHit == null) thumbHit = new GUIStyle { fixedWidth = 14f, fixedHeight = 14f };
            return thumbHit;
        }

        /// <summary>Drag on a label to change a ranged value (Unity's label-drag behavior for sliders).</summary>
        private static float LabelDrag(Rect labelRect, float value, float min, float max, float pixelsForRange)
        {
            if (!GUI.enabled) return value;
            int id = GUIUtility.GetControlID(DragHash, FocusType.Passive, labelRect);
            EditorGUIUtility.AddCursorRect(labelRect, MouseCursor.SlideArrow);
            Event e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && labelRect.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        GUIUtility.keyboardControl = 0;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        float perPixel = (max - min) / Mathf.Max(pixelsForRange, 1f);
                        value = Mathf.Clamp(value + e.delta.x * perPixel, min, max);
                        GUI.changed = true;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
            }
            return value;
        }

        /// <summary>
        /// "빠른 조절" slider card: name, readable value, 0..1 value, end words, default tick.
        /// Moves several expert values at once; the caller maps value01 to them.
        /// </summary>
        public static float QuickSlider(string name, string valueText, float value01, string leftWord, string rightWord, float default01)
        {
            Rect r = GUILayoutUtility.GetRect(0f, 62f, GUILayout.ExpandWidth(true));
            SRPalette p = SRTheme.P;
            SRTheme.Box(r, p.SurfaceCard, p.LineSubtle, SRMetrics.RadiusCard);
            Ink(new Rect(r.x + 10f, r.y + 6f, r.width * 0.6f, 18f), name, SRStyles.Section);
            GUIStyle val = SRStyles.Strong;
            Color old = val.normal.textColor;
            TextAnchor oldA = val.alignment;
            val.normal.textColor = p.BrandText;
            val.alignment = TextAnchor.MiddleRight;
            Ink(new Rect(r.x + r.width * 0.4f, r.y + 6f, r.width * 0.6f - 10f, 18f), valueText, val);
            val.normal.textColor = old;
            val.alignment = oldA;

            var track = new Rect(r.x + 19f, r.y + 29f, r.width - 38f, 6f);
            SRTheme.Fill(track, p.LineControl, 3f);
            SRTheme.Fill(new Rect(track.x, track.y, track.width * Mathf.Clamp01(value01), track.height), GUI.enabled ? p.BrandLine : p.TextDisabled, 3f);
            SRTheme.Fill(new Rect(track.x + track.width * Mathf.Clamp01(default01) - 1f, track.y - 3f, 2f, 12f), p.TextMuted, 1f);
            value01 = GUI.HorizontalSlider(new Rect(track.x - 9f, track.y - 6f, track.width + 18f, 18f), value01, 0f, 1f, GUIStyle.none, QuickThumbHit());
            var thumb = new Rect(track.x + track.width * Mathf.Clamp01(value01) - 9f, track.y - 6f, 18f, 18f);
            SRTheme.Box(thumb, GUI.enabled ? p.Brand : p.TextDisabled, p.BrandBorder, 9f);

            Ink(new Rect(r.x + 10f, r.y + 42f, r.width * 0.5f, 14f), leftWord, SRStyles.Small);
            GUIStyle right = SRStyles.Small;
            TextAnchor oldR = right.alignment;
            right.alignment = TextAnchor.MiddleRight;
            Ink(new Rect(r.x + r.width * 0.5f, r.y + 42f, r.width * 0.5f - 10f, 14f), rightWord, right);
            right.alignment = oldR;
            GUILayout.Space(SRMetrics.Space6);
            return value01;
        }

        private static GUIStyle quickThumbHit;

        private static GUIStyle QuickThumbHit()
        {
            if (quickThumbHit == null) quickThumbHit = new GUIStyle { fixedWidth = 18f, fixedHeight = 18f };
            return quickThumbHit;
        }

        /// <summary>Unity CurveField with the brand ink (Unity keeps drawing the field and editor).</summary>
        public static AnimationCurve Curve(GUIContent label, AnimationCurve curve, Rect ranges)
        {
            using (SRLabelScope.Begin())
                return EditorGUILayout.CurveField(label, curve, SRTheme.P.Brand, ranges);
        }

        // ------------------------------------------------------------ buttons

        public static bool Button(GUIContent content, SRButtonKind kind, SRButtonSize size, params GUILayoutOption[] options)
        {
            float h = size == SRButtonSize.Mini ? SRMetrics.ButtonMini : size == SRButtonSize.Large ? SRMetrics.ButtonLarge : size == SRButtonSize.Hero ? SRMetrics.ButtonHero : SRMetrics.Button;
            GUIStyle ts = SRStyles.ButtonText;
            float w = ts.CalcSize(content).x + (size == SRButtonSize.Hero ? 16f : 4f);
            Rect r = GUILayoutUtility.GetRect(w, h, options.Length > 0 ? options : new[] { GUILayout.Width(w), GUILayout.Height(h) });
            r.height = h;
            return Button(r, content, kind, size);
        }

        public static bool Button(Rect r, GUIContent content, SRButtonKind kind, SRButtonSize size)
        {
            int id = GUIUtility.GetControlID(ButtonHash, FocusType.Keyboard, r);
            Event e = Event.current;
            bool enabled = GUI.enabled;
            bool clicked = false;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (enabled && e.button == 0 && r.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                        if (r.Contains(e.mousePosition)) clicked = true;
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id) e.Use();
                    break;
                case EventType.KeyDown:
                    if (enabled && GUIUtility.keyboardControl == id && (e.keyCode == KeyCode.Space || e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
                    {
                        clicked = true;
                        e.Use();
                    }
                    break;
                case EventType.Repaint:
                    DrawButtonFace(r, content, kind, size, id, enabled);
                    // GUIStyle.Draw does not register tooltips; GUI.Label does (hover + visible clip only).
                    if (content != null && !string.IsNullOrEmpty(content.tooltip))
                    {
                        TipContent.tooltip = content.tooltip;
                        GUI.Label(r, TipContent, GUIStyle.none);
                    }
                    break;
            }
            if (clicked) GUI.changed = true;
            return clicked;
        }

        private static readonly GUIContent TipContent = new GUIContent(string.Empty);

        // face variants for the call in progress (set by ToggleButton / IconButton, read by DrawButtonFace)
        private static bool faceToggleOn;
        private static int faceGlyph = -1;

        /// <summary>
        /// Button that stays pressed while on (mode switches, view filters). Returns the new state.
        /// Off: secondary face. On: tint face, brand-line frame and bold text, so the state reads
        /// without relying on the tint alone.
        /// </summary>
        public static bool ToggleButton(GUIContent content, bool on, SRButtonSize size = SRButtonSize.Default, params GUILayoutOption[] options)
        {
            float h = size == SRButtonSize.Mini ? SRMetrics.ButtonMini : size == SRButtonSize.Large ? SRMetrics.ButtonLarge : SRMetrics.Button;
            GUIStyle ts = SRStyles.ButtonText;
            FontStyle old = ts.fontStyle;
            ts.fontStyle = FontStyle.Bold; // measure bold so the width does not jump when it turns on
            float w = ts.CalcSize(content).x + 4f;
            ts.fontStyle = old;
            Rect r = GUILayoutUtility.GetRect(w, h, options.Length > 0 ? options : new[] { GUILayout.Width(w), GUILayout.Height(h) });
            r.height = h;
            return ToggleButton(r, content, on, size);
        }

        public static bool ToggleButton(Rect r, GUIContent content, bool on, SRButtonSize size = SRButtonSize.Default)
        {
            faceToggleOn = on;
            bool clicked;
            try { clicked = Button(r, content, SRButtonKind.Secondary, size); }
            finally { faceToggleOn = false; }
            return clicked ? !on : on;
        }

        /// <summary>
        /// Square icon-only button. content.image is drawn centred (Unity icons: EditorGUIUtility.IconContent).
        /// content.tooltip is required: an icon-only button must say what it does on hover.
        /// </summary>
        public static bool IconButton(GUIContent content, SRButtonKind kind = SRButtonKind.Ghost, SRButtonSize size = SRButtonSize.Default)
        {
            float s = size == SRButtonSize.Mini ? SRMetrics.ButtonMini : size == SRButtonSize.Large ? SRMetrics.ButtonLarge : SRMetrics.Button;
            Rect r = GUILayoutUtility.GetRect(s, s, GUILayout.Width(s), GUILayout.Height(s));
            return IconButton(r, content, kind);
        }

        public static bool IconButton(Rect r, GUIContent content, SRButtonKind kind = SRButtonKind.Ghost)
        {
            return Button(r, content, kind, SRButtonSize.Mini);
        }

        /// <summary>Square button showing one of the font-independent glyphs.</summary>
        public static bool IconButton(Rect r, SRGlyph glyph, string tooltip, SRButtonKind kind = SRButtonKind.Ghost)
        {
            faceGlyph = (int)glyph;
            try { return Button(r, new GUIContent(string.Empty, tooltip), kind, SRButtonSize.Mini); }
            finally { faceGlyph = -1; }
        }

        private static void DrawButtonFace(Rect r, GUIContent content, SRButtonKind kind, SRButtonSize size, int id, bool enabled)
        {
            SRPalette p = SRTheme.P;
            bool hover = enabled && r.Contains(Event.current.mousePosition);
            bool down = GUIUtility.hotControl == id;
            bool focus = GUIUtility.keyboardControl == id;
            Color face, edge, text;
            switch (kind)
            {
                case SRButtonKind.Primary:
                    face = down ? p.BrandPressed : hover ? p.BrandHover : p.Brand;
                    edge = p.BrandBorder;
                    text = p.TextOnBrand;
                    break;
                case SRButtonKind.Ghost:
                    face = down ? p.ControlPressed : hover ? p.SurfaceHover : Color.clear;
                    edge = Color.clear;
                    text = p.Text;
                    break;
                case SRButtonKind.Danger:
                    face = p.DangerBg;
                    edge = hover ? p.Danger : p.DangerBorder;
                    text = p.DangerText;
                    break;
                default:
                    face = down ? p.ControlPressed : hover ? p.ControlHover : p.Control;
                    edge = p.LineControl;
                    text = p.TextStrong;
                    if (faceToggleOn)
                    {
                        face = down ? p.ControlPressed : p.BrandTint;
                        edge = p.BrandLine;
                    }
                    break;
            }
            if (!enabled)
            {
                face = kind == SRButtonKind.Ghost ? Color.clear : p.SurfaceCard;
                edge = kind == SRButtonKind.Ghost ? Color.clear : p.LineSubtle;
                text = p.TextDisabled;
                if (faceToggleOn) edge = p.TextDisabled;
            }
            SRTheme.Box(r, face, focus ? p.Focus : edge, SRMetrics.RadiusButton);

            bool iconOnly = content != null && content.image != null && string.IsNullOrEmpty(content.text);
            if (faceGlyph >= 0)
            {
                float g = Mathf.Min(12f, r.height - 6f);
                Glyph(new Rect(r.center.x - g * 0.5f, r.center.y - g * 0.5f, g, g), (SRGlyph)faceGlyph, text);
                return;
            }
            if (iconOnly)
            {
                float s = Mathf.Min(16f, r.height - 4f);
                Color oldGui = GUI.color;
                if (!enabled) GUI.color = new Color(oldGui.r, oldGui.g, oldGui.b, oldGui.a * 0.5f);
                GUI.DrawTexture(new Rect(r.center.x - s * 0.5f, r.center.y - s * 0.5f, s, s), content.image, ScaleMode.ScaleToFit);
                GUI.color = oldGui;
                return;
            }

            GUIStyle ts = SRStyles.ButtonText;
            Color oldColor = ts.normal.textColor;
            FontStyle oldFont = ts.fontStyle;
            int oldSize = ts.fontSize;
            // every state: a button disabled while it holds keyboard focus draws the focused state
            SRStyles.SetTextColor(ts, text);
            ts.fontStyle = kind == SRButtonKind.Primary || faceToggleOn ? FontStyle.Bold : FontStyle.Normal;
            ts.fontSize = size == SRButtonSize.Mini ? SRMetrics.FontSmall : SRMetrics.FontBody;
            // disabled: text is already text-disabled, so suspend Unity's half-alpha blend for this
            // draw only (Repaint, no input handled here)
            GUI.enabled = true;
            try { ts.Draw(r, content, id, false, hover); }
            finally
            {
                GUI.enabled = enabled;
                SRStyles.SetTextColor(ts, oldColor);
                ts.fontStyle = oldFont;
                ts.fontSize = oldSize;
            }
        }

        /// <summary>2-4 mutually exclusive options (Unity dependent toggles).</summary>
        public static int Segmented(GUIContent label, int index, GUIContent[] options)
        {
            Rect row = EditorGUILayout.GetControlRect(true, SRMetrics.Button);
            SRPalette p = SRTheme.P;
            Rect field = row;
            if (label != null && label != GUIContent.none)
            {
                using (SRLabelScope.Begin()) EditorGUI.LabelField(new Rect(row.x, row.y, EditorGUIUtility.labelWidth, row.height), label);
                field = FieldPart(row);
            }
            bool enabled = GUI.enabled;
            // disabled: card face like a disabled button (text-disabled on control is 2.3:1 in dark)
            SRTheme.Box(field, enabled ? p.Control : p.SurfaceCard, enabled ? p.LineControl : p.LineSubtle, SRMetrics.RadiusButton);
            float w = field.width / Mathf.Max(1, options.Length);
            for (int i = 0; i < options.Length; i++)
            {
                var c = new Rect(field.x + w * i, field.y, w, field.height);
                bool on = i == index;
                if (on)
                {
                    // tint alone is ~1.1-1.35:1 against the control face; the underline carries the state (3:1)
                    if (enabled) SRTheme.Fill(new Rect(c.x + 1f, c.y + 1f, c.width - 2f, c.height - 2f), p.BrandTint, SRMetrics.RadiusButton - 1f);
                    SRTheme.Fill(new Rect(c.x + 3f, c.yMax - 1f - SRMetrics.AccentLine, c.width - 6f, SRMetrics.AccentLine), enabled ? p.BrandLine : p.TextDisabled, 1f);
                }
                if (i > 0) SRTheme.Fill(new Rect(c.x, c.y, 1f, c.height), enabled ? p.LineControl : p.LineSubtle);
                GUIStyle ts = on ? SRStyles.Strong : SRStyles.Body;
                TextAnchor old = ts.alignment;
                ts.alignment = TextAnchor.MiddleCenter;
                Ink(c, options[i], ts);
                ts.alignment = old;
                if (GUI.Button(c, GUIContent.none, GUIStyle.none)) index = i;
            }
            return index;
        }

        private static int searchFocusId;

        /// <summary>Rounded search box with placeholder. The × button and Esc clear it.</summary>
        public static string SearchField(string value, string placeholder, params GUILayoutOption[] options)
        {
            return SearchField(null, value, placeholder, options);
        }

        /// <summary>Search box with a caller-chosen control name (for GUI.FocusControl). Null name = generated.</summary>
        public static string SearchField(string controlName, string value, string placeholder, params GUILayoutOption[] options)
        {
            Rect r = EditorGUILayout.GetControlRect(false, SRMetrics.Button, options);
            SRPalette p = SRTheme.P;
            Event e = Event.current;
            value = value ?? string.Empty;
            string name = string.IsNullOrEmpty(controlName) ? NextFieldName() : controlName;
            float radius = r.height * 0.5f;
            SRTheme.Box(r, p.SurfaceSunken, p.LineControl, radius);
            Glyph(new Rect(r.x + 6f, r.y + (r.height - 12f) * 0.5f, 12f, 12f), SRGlyph.Search, GUI.enabled ? p.TextMuted : p.TextDisabled);

            bool hasText = value.Length > 0;
            var clear = new Rect(r.xMax - 18f, r.y + (r.height - 14f) * 0.5f, 14f, 14f);
            var text = new Rect(r.x + 20f, r.y + 1f, r.width - 26f - (hasText ? 14f : 0f), r.height - 2f);
            if (hasText && GUI.enabled)
            {
                if (e.type == EventType.Repaint && clear.Contains(e.mousePosition)) SRTheme.Fill(clear, p.SurfaceHover, 7f);
                Glyph(new Rect(clear.x + 3f, clear.y + 3f, 8f, 8f), SRGlyph.Close, p.TextMuted);
                EditorGUIUtility.AddCursorRect(clear, MouseCursor.Arrow);
                if (GUI.Button(clear, GUIContent.none, GUIStyle.none))
                {
                    value = string.Empty;
                    GUI.changed = true;
                    GUIUtility.keyboardControl = 0;
                }
            }

            // Focus from the previous event: EditorGUI.TextField itself consumes Escape (revert), so
            // the clear-on-Esc check must run before the field is drawn.
            bool hadFocus = searchFocusId != 0 && GUIUtility.keyboardControl == searchFocusId;
            if (hadFocus && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                value = string.Empty;
                GUI.changed = true;
                GUIUtility.keyboardControl = 0;
                e.Use();
            }
            SetFieldInk(); // FieldText is shared: without this the colour of the last field drawn leaks in
            GUI.SetNextControlName(name);
            value = EditorGUI.TextField(text, value, SRStyles.FieldText);
            bool focus = GUI.enabled && GUI.GetNameOfFocusedControl() == name;
            if (focus) searchFocusId = GUIUtility.keyboardControl;
            if (focus)
            {
                SRTheme.Stroke(r, p.Focus, radius, 1f);
                SRTheme.Stroke(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, r.height - 2f), p.Focus, radius - 1f, 1f);
            }
            else if (value.Length == 0)
            {
                Ink(text, placeholder, SRStyles.Muted);
            }
            return value;
        }

        // ------------------------------------------------------------ drop area

        private static readonly int DropHash = "SRGui.DropArea".GetHashCode();

        /// <summary>
        /// Drop zone for assets or scene objects. Returns the accepted objects on drop, otherwise null.
        /// accept filters each object (null = accept all); a drag with nothing acceptable shows "rejected".
        /// </summary>
        public static UnityEngine.Object[] DropArea(GUIContent label, float height, Func<UnityEngine.Object, bool> accept = null)
        {
            Rect r = GUILayoutUtility.GetRect(0f, height, GUILayout.ExpandWidth(true));
            return DropArea(r, label, accept);
        }

        public static UnityEngine.Object[] DropArea(Rect r, GUIContent label, Func<UnityEngine.Object, bool> accept = null)
        {
            int id = GUIUtility.GetControlID(DropHash, FocusType.Passive, r);
            Event e = Event.current;
            SRPalette p = SRTheme.P;
            UnityEngine.Object[] dragged = DragAndDrop.objectReferences;
            bool over = GUI.enabled && r.Contains(e.mousePosition) && dragged != null && dragged.Length > 0;
            UnityEngine.Object[] dropped = null;
            switch (e.type)
            {
                case EventType.DragUpdated:
                    if (over)
                    {
                        DragAndDrop.visualMode = CountAccepted(dragged, accept) > 0 ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                        DragAndDrop.activeControlID = id;
                        e.Use();
                    }
                    else if (DragAndDrop.activeControlID == id)
                    {
                        DragAndDrop.activeControlID = 0;
                    }
                    break;
                case EventType.DragPerform:
                    if (over && CountAccepted(dragged, accept) > 0)
                    {
                        DragAndDrop.AcceptDrag();
                        dropped = new UnityEngine.Object[CountAccepted(dragged, accept)];
                        int n = 0;
                        foreach (UnityEngine.Object o in dragged)
                            if (o != null && (accept == null || accept(o))) dropped[n++] = o;
                        DragAndDrop.activeControlID = 0;
                        GUI.changed = true;
                        e.Use();
                    }
                    break;
                case EventType.DragExited:
                    if (DragAndDrop.activeControlID == id) DragAndDrop.activeControlID = 0;
                    break;
                case EventType.Repaint:
                    bool active = DragAndDrop.activeControlID == id && over;
                    bool ok = active && DragAndDrop.visualMode != DragAndDropVisualMode.Rejected;
                    if (ok)
                    {
                        SRTheme.Box(r, p.BrandTint, p.BrandLine, SRMetrics.RadiusCard);
                        SRTheme.Stroke(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, r.height - 2f), p.BrandLine, SRMetrics.RadiusCard - 1f, 1f);
                    }
                    else
                    {
                        SRTheme.Fill(r, active ? p.DangerBg : p.SurfaceSunken, SRMetrics.RadiusCard);
                        DashedBorder(r, active ? p.Danger : p.LineStrong);
                    }
                    GUIStyle s = SRStyles.Center;
                    Color old = s.normal.textColor;
                    s.normal.textColor = ok ? p.TextStrong : p.TextMuted;
                    Ink(new Rect(r.x + 8f, r.y + 4f, r.width - 16f, r.height - 8f), label, s);
                    s.normal.textColor = old;
                    break;
            }
            return dropped;
        }

        private static int CountAccepted(UnityEngine.Object[] objects, Func<UnityEngine.Object, bool> accept)
        {
            int n = 0;
            if (objects == null) return 0;
            foreach (UnityEngine.Object o in objects)
                if (o != null && (accept == null || accept(o))) n++;
            return n;
        }

        /// <summary>4px dashes, 3px gaps along the rect edge (square corners inset by the card radius).</summary>
        private static void DashedBorder(Rect r, Color c)
        {
            const float dash = 4f, gap = 3f, inset = 4f;
            for (float x = r.x + inset; x < r.xMax - inset; x += dash + gap)
            {
                float w = Mathf.Min(dash, r.xMax - inset - x);
                SRTheme.Fill(new Rect(x, r.y, w, 1f), c);
                SRTheme.Fill(new Rect(x, r.yMax - 1f, w, 1f), c);
            }
            for (float y = r.y + inset; y < r.yMax - inset; y += dash + gap)
            {
                float h = Mathf.Min(dash, r.yMax - inset - y);
                SRTheme.Fill(new Rect(r.x, y, 1f, h), c);
                SRTheme.Fill(new Rect(r.xMax - 1f, y, 1f, h), c);
            }
        }

        // ------------------------------------------------------------ glyphs

        /// <summary>
        /// Font-independent glyphs drawn as rounded bars (the Unity 2021.3 editor font lacks ⌕ and may
        /// lack ✓ ▾ ▸). Rotated with GUI.matrix, so they clip and scroll like any IMGUI draw.
        /// </summary>
        public static void Glyph(Rect r, SRGlyph glyph, Color color, float thickness = 1.6f)
        {
            if (Event.current.type != EventType.Repaint) return;
            float s = Mathf.Min(r.width, r.height) / 12f;
            var o = new Vector2(r.center.x - 6f * s, r.center.y - 6f * s);
            float t = thickness * Mathf.Max(0.75f, s);
            switch (glyph)
            {
                case SRGlyph.Check:
                    Segment(o + new Vector2(2.6f, 6.4f) * s, o + new Vector2(5.0f, 8.8f) * s, color, t);
                    Segment(o + new Vector2(5.0f, 8.8f) * s, o + new Vector2(9.6f, 3.4f) * s, color, t);
                    break;
                case SRGlyph.ChevronDown:
                    Segment(o + new Vector2(3.0f, 4.6f) * s, o + new Vector2(6.0f, 7.6f) * s, color, t);
                    Segment(o + new Vector2(6.0f, 7.6f) * s, o + new Vector2(9.0f, 4.6f) * s, color, t);
                    break;
                case SRGlyph.ChevronRight:
                    Segment(o + new Vector2(4.6f, 3.0f) * s, o + new Vector2(7.6f, 6.0f) * s, color, t);
                    Segment(o + new Vector2(7.6f, 6.0f) * s, o + new Vector2(4.6f, 9.0f) * s, color, t);
                    break;
                case SRGlyph.Close:
                    Segment(o + new Vector2(3.0f, 3.0f) * s, o + new Vector2(9.0f, 9.0f) * s, color, t);
                    Segment(o + new Vector2(9.0f, 3.0f) * s, o + new Vector2(3.0f, 9.0f) * s, color, t);
                    break;
                case SRGlyph.Search:
                    float d = 7.2f * s;
                    SRTheme.Stroke(new Rect(o.x + 1.2f * s, o.y + 1.2f * s, d, d), color, d * 0.5f, t);
                    Segment(o + new Vector2(7.6f, 7.6f) * s, o + new Vector2(10.6f, 10.6f) * s, color, t);
                    break;
            }
        }

        /// <summary>Line segment as a rounded bar rotated about its midpoint.</summary>
        public static void Segment(Vector2 a, Vector2 b, Color color, float thickness)
        {
            if (Event.current.type != EventType.Repaint) return;
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) return;
            Vector2 mid = (a + b) * 0.5f;
            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, mid);
            SRTheme.Fill(new Rect(mid.x - len * 0.5f - thickness * 0.5f, mid.y - thickness * 0.5f, len + thickness, thickness), color, thickness * 0.5f);
            GUI.matrix = old;
        }

        // ------------------------------------------------------------ feedback

        public static float BadgeWidth(string text)
        {
            return SRStyles.Mini.CalcSize(new GUIContent(text)).x + 14f;
        }

        public static void Badge(Rect r, string text, SRTone tone)
        {
            SRToneColors c = SRTheme.Tone(tone);
            SRTheme.Box(r, c.Bg, c.Border, SRMetrics.RadiusPill);
            GUIStyle s = SRStyles.Mini;
            Color old = s.normal.textColor;
            s.normal.textColor = tone == SRTone.Brand ? SRTheme.P.TextStrong : c.Text;
            GUI.Label(r, text, s); // tone fill: keeps Unity's blend (see Ink)
            s.normal.textColor = old;
        }

        public static void BadgeLayout(string text, SRTone tone)
        {
            float w = BadgeWidth(text);
            Rect r = GUILayoutUtility.GetRect(w, 18f, GUILayout.Width(w), GUILayout.Height(18f));
            Badge(r, text, tone);
        }

        // ------------------------------------------------------------ chips

        private static readonly int ChipHash = "SRGui.Chip".GetHashCode();

        public static float ChipWidth(string text, bool removable)
        {
            return SRStyles.Small.CalcSize(new GUIContent(text)).x + 16f + (removable ? 16f : 0f);
        }

        /// <summary>
        /// Small tag for an applied filter or a picked item (20px pill). With removable, a × at the
        /// right removes it: returns true when the × is clicked.
        /// </summary>
        public static bool Chip(Rect r, string text, bool removable)
        {
            SRPalette p = SRTheme.P;
            // disabled: card face like a disabled button (text-disabled on control is 2.3:1 in dark)
            if (GUI.enabled) SRTheme.Box(r, p.Control, p.LineControl, SRMetrics.RadiusPill);
            else SRTheme.Box(r, p.SurfaceCard, p.LineSubtle, SRMetrics.RadiusPill);
            GUIStyle s = SRStyles.Small;
            Color old = s.normal.textColor;
            TextAnchor oldA = s.alignment;
            s.normal.textColor = p.Text;
            s.alignment = TextAnchor.MiddleLeft;
            Ink(new Rect(r.x + 8f, r.y, r.width - 16f - (removable ? 16f : 0f), r.height), text, s);
            s.normal.textColor = old;
            s.alignment = oldA;
            if (!removable) return false;
            var x = new Rect(r.xMax - 20f, r.y + (r.height - 14f) * 0.5f, 14f, 14f);
            if (GUI.enabled && Event.current.type == EventType.Repaint && x.Contains(Event.current.mousePosition))
                SRTheme.Fill(x, p.SurfaceHover, 7f);
            Glyph(new Rect(x.x + 3f, x.y + 3f, 8f, 8f), SRGlyph.Close, GUI.enabled ? p.TextMuted : p.TextDisabled);
            GUIUtility.GetControlID(ChipHash, FocusType.Passive, r);
            return GUI.Button(x, GUIContent.none, GUIStyle.none);
        }

        public static bool ChipLayout(string text, bool removable)
        {
            float w = ChipWidth(text, removable);
            Rect r = GUILayoutUtility.GetRect(w, SRMetrics.Button, GUILayout.Width(w), GUILayout.Height(SRMetrics.Button));
            return Chip(r, text, removable);
        }

        public static float ColorChipWidth(string text)
        {
            return SRStyles.Small.CalcSize(new GUIContent(text)).x + 34f;
        }

        /// <summary>
        /// Colour swatch + name in a pill (texture colours, material slots). Returns true on click.
        /// Selected: tint face and brand-line frame.
        /// </summary>
        public static bool ColorChip(Rect r, Color color, string text, bool selected)
        {
            SRPalette p = SRTheme.P;
            bool hover = GUI.enabled && r.Contains(Event.current.mousePosition);
            Color face = selected ? p.BrandTint : hover ? p.ControlHover : p.Control;
            Color edge = selected ? p.BrandLine : p.LineControl;
            if (!GUI.enabled)
            {
                // card face like a disabled toggle button; a selected chip keeps a text-disabled frame
                face = p.SurfaceCard;
                edge = selected ? p.TextDisabled : p.LineSubtle;
            }
            SRTheme.Box(r, face, edge, SRMetrics.RadiusPill);
            var sw = new Rect(r.x + 5f, r.y + (r.height - 12f) * 0.5f, 12f, 12f);
            SRTheme.Box(sw, new Color(color.r, color.g, color.b, 1f), p.LineStrong, 6f);
            GUIStyle s = selected ? SRStyles.Strong : SRStyles.Small;
            Color old = s.normal.textColor;
            TextAnchor oldA = s.alignment;
            if (!selected) s.normal.textColor = p.Text;
            s.alignment = TextAnchor.MiddleLeft;
            Ink(new Rect(sw.xMax + 6f, r.y, r.width - 29f, r.height), text, s);
            s.normal.textColor = old;
            s.alignment = oldA;
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        public static bool ColorChipLayout(Color color, string text, bool selected)
        {
            float w = ColorChipWidth(text);
            Rect r = GUILayoutUtility.GetRect(w, SRMetrics.Button, GUILayout.Width(w), GUILayout.Height(SRMetrics.Button));
            return ColorChip(r, color, text, selected);
        }

        /// <summary>
        /// Help box below the control it is about. Message: problem, cause, fix; 200 chars max; no
        /// "오류:" prefix. Optional single action button. Returns true when the action is clicked.
        /// </summary>
        public static bool HelpBox(string message, SRTone tone, string actionLabel = null)
        {
            bool hasAction = !string.IsNullOrEmpty(actionLabel);
            var content = new GUIContent(message);
            // Reserve text and action independently inside the actual padded width. A MinHeight
            // option on a word-wrapped content rect can replace Unity's calculated wrap height.
            Rect r = EditorGUILayout.BeginVertical(SRStyles.HelpLayout, GUILayout.ExpandWidth(true), GUILayout.MinHeight(30f));
            HelpBoxFace(r, tone);
            Rect text = GUILayoutUtility.GetRect(content, SRStyles.Wrap, GUILayout.ExpandWidth(true));
            Ink(text, content, SRStyles.Wrap);
            bool clicked = false;
            if (hasAction)
            {
                GUILayout.Space(3f);
                var ac = new GUIContent(actionLabel);
                float bw = SRStyles.ButtonText.CalcSize(ac).x;
                Rect action = GUILayoutUtility.GetRect(bw, SRMetrics.ButtonMini, GUILayout.Width(bw), GUILayout.Height(SRMetrics.ButtonMini));
                clicked = Button(action, ac, SRButtonKind.Secondary, SRButtonSize.Mini);
            }
            GUILayout.EndVertical();
            GUILayout.Space(SRMetrics.Space4);
            return clicked;
        }

        /// <summary>Help box in a fixed rect. Size it with HelpBoxHeight or SRStyles.HelpLayout.</summary>
        public static bool HelpBox(Rect r, GUIContent content, SRTone tone, string actionLabel = null)
        {
            bool hasAction = !string.IsNullOrEmpty(actionLabel);
            RectOffset pad = (hasAction ? SRStyles.HelpActionLayout : SRStyles.HelpLayout).padding;
            HelpBoxFace(r, tone);
            var text = new Rect(r.x + pad.left, r.y + pad.top, r.width - pad.horizontal, Mathf.Max(16f, r.height - pad.vertical));
            Ink(text, content, SRStyles.Wrap);
            if (!hasAction) return false;
            var ac = new GUIContent(actionLabel);
            float bw = SRStyles.ButtonText.CalcSize(ac).x;
            return Button(new Rect(text.x, text.yMax + 3f, bw, SRMetrics.ButtonMini), ac, SRButtonKind.Secondary, SRButtonSize.Mini);
        }

        private static void HelpBoxFace(Rect r, SRTone tone)
        {
            SRToneColors c = SRTheme.Tone(tone);
            SRPalette p = SRTheme.P;
            SRTheme.Box(r, c.Bg, c.Border, SRMetrics.RadiusButton);

            var icon = new Rect(r.x + 8f, r.y + 7f, 16f, 16f);
            SRTheme.Fill(icon, c.Mark, 8f);
            if (tone == SRTone.Success || tone == SRTone.Danger)
            {
                Glyph(new Rect(icon.x + 2f, icon.y + 2f, 12f, 12f), tone == SRTone.Success ? SRGlyph.Check : SRGlyph.Close, p.SurfaceWindow, 1.8f);
            }
            else
            {
                GUIStyle glyph = SRStyles.Mini;
                Color old = glyph.normal.textColor;
                glyph.normal.textColor = p.SurfaceWindow;
                GUI.Label(icon, tone == SRTone.Warning ? "!" : "i", glyph);
                glyph.normal.textColor = old;
            }

        }

        /// <summary>Height a help box needs at a given width (for rect-based layouts).</summary>
        public static float HelpBoxHeight(string message, float width, bool hasAction = false)
        {
            RectOffset pad = (hasAction ? SRStyles.HelpActionLayout : SRStyles.HelpLayout).padding;
            float textHeight = Mathf.Max(16f, SRStyles.Wrap.CalcHeight(new GUIContent(message), Mathf.Max(1f, width - pad.horizontal)));
            return Mathf.Max(30f, textHeight + pad.vertical);
        }

        /// <summary>Progress line: name + step on the left, percent on the right, 6px bar below.</summary>
        public static void ProgressBar(float progress01, string label, SRTone tone = SRTone.Info)
        {
            Rect r = GUILayoutUtility.GetRect(0f, 24f, GUILayout.ExpandWidth(true));
            SRPalette p = SRTheme.P;
            Ink(new Rect(r.x, r.y, r.width - 48f, 14f), label, SRStyles.Small);
            GUIStyle pct = SRStyles.Strong;
            TextAnchor old = pct.alignment;
            pct.alignment = TextAnchor.MiddleRight;
            Ink(new Rect(r.xMax - 48f, r.y, 48f, 14f), Mathf.RoundToInt(Mathf.Clamp01(progress01) * 100f) + "%", pct);
            pct.alignment = old;
            var bar = new Rect(r.x, r.y + 17f, r.width, 6f);
            SRTheme.Box(bar, p.SurfaceSunken, p.LineControl, 3f);
            Color fill = tone == SRTone.Success ? p.Success : tone == SRTone.Danger ? p.Danger : p.Mint;
            SRTheme.Fill(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(progress01), bar.height), fill, 3f);
        }

        /// <summary>List row face: alternate, hover, selected (whole face, no side bar).</summary>
        public static void RowBackground(Rect r, bool selected, bool hover, bool alternate)
        {
            SRPalette p = SRTheme.P;
            Color c = selected ? p.BrandTint : hover ? p.SurfaceHover : alternate ? p.SurfaceWindow : p.SurfaceCard;
            SRTheme.Fill(r, c);
            SRTheme.Fill(new Rect(r.x, r.yMax - 1f, r.width, 1f), p.LineSubtle);
            // the tint is too close to the card colour to mark selection alone; a 1px frame carries it (3:1)
            if (selected) SRTheme.Stroke(r, p.BrandLine, 0f, 1f);
        }

        /// <summary>Centered empty state: title, one or two sentences, one mini action. Returns true on click.</summary>
        public static bool EmptyState(string title, string message, string actionLabel, float minHeight = 180f)
        {
            Rect r = GUILayoutUtility.GetRect(0f, minHeight, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            SRPalette p = SRTheme.P;
            SRTheme.Box(r, p.SurfaceCard, p.LineControl, SRMetrics.RadiusCard);
            float w = Mathf.Min(300f, r.width - 32f);
            float th = SRStyles.Hero.CalcHeight(new GUIContent(title), w);
            float mh = SRStyles.Center.CalcHeight(new GUIContent(message), w);
            float total = th + 8f + mh + (string.IsNullOrEmpty(actionLabel) ? 0f : 8f + SRMetrics.ButtonMini);
            float y = r.y + Mathf.Max(16f, (r.height - total) * 0.5f);
            float x = r.x + (r.width - w) * 0.5f;
            Ink(new Rect(x, y, w, th), title, SRStyles.Hero);
            y += th + 8f;
            Ink(new Rect(x, y, w, mh), message, SRStyles.Center);
            y += mh + 8f;
            if (string.IsNullOrEmpty(actionLabel)) return false;
            var ac = new GUIContent(actionLabel);
            float bw = SRStyles.ButtonText.CalcSize(ac).x;
            return Button(new Rect(r.x + (r.width - bw) * 0.5f, y, bw, SRMetrics.ButtonMini), ac, SRButtonKind.Secondary, SRButtonSize.Mini);
        }

        /// <summary>Bottom-left hint in the Scene view (theme independent). Call from duringSceneGui.</summary>
        public static void SceneHint(SceneView view, string text)
        {
            if (view == null || string.IsNullOrEmpty(text)) return;
            Handles.BeginGUI();
            SRPalette p = SRTheme.P;
            GUIStyle s = SRStyles.Small;
            Color old = s.normal.textColor;
            s.normal.textColor = p.SceneHintText;
            var content = new GUIContent(text);
            Vector2 size = s.CalcSize(content);
            var r = new Rect(10f, view.position.height - size.y - 8f - 10f - 22f, size.x + 20f, size.y + 8f);
            SRTheme.Fill(r, p.SceneHintBg, SRMetrics.RadiusButton);
            GUI.Label(new Rect(r.x + 10f, r.y + 4f, size.x, size.y), content, s);
            s.normal.textColor = old;
            Handles.EndGUI();
        }

        /// <summary>
        /// Label content clipped with an ellipsis to maxWidth. When clipped and the content has no
        /// tooltip, the full text becomes the tooltip (Unity rule for truncated labels).
        /// </summary>
        public static GUIContent FitContent(GUIContent content, GUIStyle style, float maxWidth)
        {
            if (content == null || string.IsNullOrEmpty(content.text) || style.CalcSize(content).x <= maxWidth) return content;
            // tooltips do not render rich text, so the full-text tooltip drops the tags
            string tip = !string.IsNullOrEmpty(content.tooltip) ? content.tooltip
                : style.richText ? RichTag.Replace(content.text, string.Empty) : content.text;
            return new GUIContent(Fit(content.text, style, maxWidth), content.image, tip);
        }

        // Rich-text tags Unity's IMGUI understands (color, b, i, size).
        private static readonly Regex RichTag = new Regex("</?(color|b|i|size)(=[^>]*)?>", RegexOptions.IgnoreCase);

        /// <summary>
        /// Clips text with an ellipsis so it fits maxWidth. With a rich-text style the cut falls only
        /// between visible characters, and tags still open at the cut are closed before the ellipsis,
        /// so "번역 (원어)" labels never show half a colour tag.
        /// </summary>
        public static string Fit(string text, GUIStyle style, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return fitCache.Fit(text, style, FitLook(style), maxWidth, FitUncachedFunc);
        }

        // lists redraw every row on every repaint and a clipped name costs a binary search of native measurements: the result is remembered
        private static readonly FitTextCache<GUIStyle> fitCache = new FitTextCache<GUIStyle>();
        private static readonly System.Func<string, GUIStyle, float, string> FitUncachedFunc = FitUncached;

        /// <summary>Every style property that changes a measurement, as one number: a changed style is a new cache entry.</summary>
        private static int FitLook(GUIStyle style)
        {
            unchecked
            {
                int h = style.fontSize;
                h = h * 31 + (int)style.fontStyle;
                h = h * 31 + (style.richText ? 1 : 0);
                h = h * 31 + (style.font != null ? style.font.GetInstanceID() : 0);
                h = h * 31 + style.padding.left * 64 + style.padding.right;
                return h * 31 + EditorGUIUtility.pixelsPerPoint.GetHashCode();
            }
        }

        private static string FitUncached(string text, GUIStyle style, float maxWidth)
        {
            if (style.CalcSize(new GUIContent(text)).x <= maxWidth) return text;
            const string ell = "…";
            if (!style.richText || text.IndexOf('<') < 0)
            {
                int lo = 0, hi = text.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (style.CalcSize(new GUIContent(text.Substring(0, mid) + ell)).x <= maxWidth) lo = mid;
                    else hi = mid - 1;
                }
                return text.Substring(0, lo) + ell;
            }

            // cut[k] = raw index right after the k-th visible character (cut[0] = before any)
            var cut = new System.Collections.Generic.List<int> { 0 };
            for (int i = 0; i < text.Length;)
            {
                Match m = RichTag.Match(text, i);
                if (m.Success && m.Index == i) { i += m.Length; continue; }
                i++;
                cut.Add(i);
            }
            int a = 0, b = cut.Count - 1;
            while (a < b)
            {
                int mid = (a + b + 1) / 2;
                if (style.CalcSize(new GUIContent(ClosedPrefix(text, cut[mid]) + ell)).x <= maxWidth) a = mid;
                else b = mid - 1;
            }
            return ClosedPrefix(text, cut[a]) + ell;
        }

        /// <summary>text[0..length) with every rich-text tag still open at the end closed, innermost first.</summary>
        private static string ClosedPrefix(string text, int length)
        {
            string prefix = text.Substring(0, length);
            var open = new System.Collections.Generic.List<string>();
            foreach (Match m in RichTag.Matches(prefix))
            {
                string name = m.Groups[1].Value.ToLowerInvariant();
                if (m.Value[1] != '/') open.Add(name);
                else
                {
                    int at = open.LastIndexOf(name);
                    if (at >= 0) open.RemoveAt(at);
                }
            }
            for (int i = open.Count - 1; i >= 0; i--) prefix += "</" + open[i] + ">";
            return prefix;
        }
    }
}
