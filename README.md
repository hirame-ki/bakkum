# 바꿈

선생님을 위한 윈도우용 파일 변환 도구입니다. 탐색기에서 파일을 끌다가 **Shift**를 누르면 커서 자리에 메뉴가 뜨고, 원하는 칸에 파일을 놓으면 바뀝니다.

- 결과는 원본 옆에 새 파일로 만듭니다. 원본은 바꾸지 않고, 이름이 겹치면 `이름 (2)`처럼 번호를 붙입니다.
- PDF·그림은 윈도우에 들어 있는 기능으로 이 PC 안에서 바꿉니다.
- 한글·워드·엑셀·PPT 파일은 PC에 설치된 한컴오피스나 MS 오피스를 불러 바꿉니다.
- 영상·소리·화면 녹화도 윈도우에 들어 있는 기능만 씁니다. 따로 깔 프로그램이 없습니다.
- AI는 'AI로' 칸이나 '글자 뽑기 → AI로'를 고를 때만 쓰고, 보낼 때마다 먼저 묻습니다. 사진 가리기는 이 PC 안에서만 합니다.
- 하루 한 번 새 버전이 있는지 버전 번호만 확인합니다(설정에서 끌 수 있습니다). 문서 내용은 보내지 않습니다.

## 할 수 있는 일

| 칸 | 넣는 파일 | 결과 |
|---|---|---|
| PDF로 | 한글·워드·엑셀·PPT·그림 | PDF (여러 개는 하나로 묶거나 따로) |
| 그림으로 | PDF·한글·워드·엑셀·PPT | 쪽마다 PNG 또는 JPG, 문서 속 사진 뽑기 |
| 형식 바꾸기 | 그림·PDF·한글·워드·표 파일·글 파일·전자책·옛 PPT | PNG·JPG·WEBP·BMP·GIF·TIFF·ICO(SVG도 넣을 수 있음), HWP ↔ HWPX, PDF → 워드·엑셀, PDF·한글·워드 → 글자 고칠 수 있는 PPT, PDF → 그림 PPT, 한글 → 워드, 한글 표 → 엑셀, 사진 → 워드·PPT, 한글·워드 → 마크다운·HTML·EPUB, MD·HTML·EPUB·워드 사이, SRT ↔ VTT, 엑셀·CSV·TSV·ODS 사이(한셀·옛 엑셀 .xls도), 한쇼·옛 PPT(.ppt) → PPTX. 결과가 쓸모없는 변환(PDF → 마크다운·HTML·EPUB, 표 → 글자 파일 등)은 고를 거리에 두지 않는다 |
| 용량 줄이기 | 그림·PDF·문서 | 조금(긴 변 3000)·보통(2000)·많이(1400)·목표 용량 |
| 글자 뽑기 | 그림·PDF·문서 | 이 PC에서(윈도우 글자 인식), 워드로, AI로(표까지) |
| 사진 손질 | 그림 | 가리기(블러·모자이크·색 칸, 전체·일부), 얼굴 가리기, 자르기·회전·뒤집기·크기, 밝기·대비·채도·선명도, 글자·화살표·형광펜, 배경·여백·둥근 모서리, 문서 펴기, 콜라주, 사진 정보 보기·지우기 |
| PDF 편집 | PDF | 쪽 편집(순서·돌리기·삭제), 합치기, 나누기, 쪽 고르기, 암호 걸기·풀기, 워터마크, 쪽 번호, 정보 지우기 |
| 영상·소리 | 영상·소리 파일 | 영상: MP4·MOV·MKV, 용량 줄이기, 영상 편집(아래), GIF, 소리만 MP3·WAV / 소리: MP3·M4A·WAV·FLAC, 자르기 |
| 화면 녹화 | (파일 없이) | 화면 전체나 고른 영역을 MP4로(영역은 자유 또는 16:9·9:16·1:1·4:5·4:3·3:4 비율로 고르고 손잡이로 크기 조절), 소리 없음·마이크·컴퓨터 소리·둘 다, 잠시 멈춤, 웹캠 같이 찍기(얼굴 영상은 따로 저장되고 영상 편집에서 동그랗게 얹힘). 비디오 폴더에 저장 |
| 압축 | 모든 파일·폴더 | ZIP·TAR로 묶기, 파일 하나 GZIP, ZIP·TAR·GZ·TGZ 풀기 |
| AI로 | 문서·그림·PDF·폴더 | 요약, 요점 정리(공문의 마감·할 일, 일정은 엑셀·달력 파일로, 공문 여러 개는 마감순 할 일표 한 장으로), 9개 언어 번역, 문서 쓰기(기안문·계획서·결과 보고서·가정 통신문 초안, 쉬운 말·맞춤법), 표 → 엑셀, 문제와 시험지를 본뜬 비슷한 문제, 학습지, 디자인을 입힌 수업 PPT(네 가지 디자인, 발표자 노트), 수준별 자료, 지도안, 채점 기준표, 손글씨 읽기, 이름 짓기, 폴더 정리(파일 이름만 보고 하위 폴더로 나누기·이름 다듬기, 옮기기 전에 고칠 수 있게 묻기) |

받는 형식: `hwp hwpx` · `doc docx rtf odt` · `xls xlsx xlsm csv cell ods` · `ppt pptx show` · `pdf` · `png jpg jpeg jfif bmp gif tif tiff webp heic heif avif svg` · `txt md markdown html htm srt vtt tsv epub` · `zip tar gz tgz` · `mp4 m4v mov mkv avi wmv asf webm mpg mpeg ts mts m2ts 3gp` · `mp3 wav m4a aac flac ogg oga opus wma`

HEIC·AVIF·WEBP 그림은 PC에 해당 이미지 확장이 있어야 열립니다. 영상·소리는 그 PC의 윈도우가 읽는 코덱만 열 수 있습니다. 휴대폰의 고효율(HEVC) 영상과 AV1·VP9 영상은 Microsoft Store의 해당 비디오 확장이 있어야 열리고, 열지 못하면 어떤 확장이 필요한지 알려 줍니다. WebM·OGG로 내보내기와 RAR·7z, 암호 걸린 압축 파일은 지원하지 않습니다.

### 영상 편집

영상 파일을 [영상·소리] → [영상 편집]에 놓으면 편집 창이 열립니다. 왼쪽 칸 메뉴 아홉 개로 나뉩니다.

| 칸 | 하는 일 |
|---|---|
| 편집 | 나누기, 구간 잘라 내기·남기기, 조각 차례 바꾸기, 빠르기(0.25~2배)·거꾸로·멈춘 화면, 넘어가는 효과, 사진 움직임, 표시(Ctrl+←·→로 이동), 긴 영상은 미리 보기용 가벼운 사본. 위 막대에 자석·되돌리기 |
| 자막 | 자동 자막(이 PC에서 받아쓰기), 자막 넣기·고치기, 강조 낱말 색, 찾아 바꾸기, 모든 자막 시간 옮기기, 읽어 주기(윈도우 목소리), SRT 불러오기·저장 |
| 글자·그림 | 제목·그림·스티커·도형·작은 화면, 움직임 표시, 내 꾸밈 저장 |
| 화면 비율 | 16:9·9:16·1:1·4:5, 맞추기·꽉 채우기·자리 옮기기 |
| 소리 | 원본 소리 다듬기(잡음 줄이기·목소리 높낮이), 배경 음악·내 목소리·효과음, 말할 때 음악 줄이기 |
| 색·효과 | 밝기·대비·채도·색온도·선명하게, 흑백 같은 느낌, 흔들림 줄이기, 손대기 전과 나란히 보기 |
| 가리기 | 흐리게·모자이크·색으로 덮기, 움직임을 따라가는 상자, 얼굴 찾아 가리기(보여도 되는 얼굴은 끄기) |
| 선생님 도구 | 조용한 곳 한 번에 자르기, 화면 녹화 다듬기(누른 곳 확대·마우스 강조), 내 얼굴 작은 화면, 수업 자료(PDF·PPT·한글)로 영상(PPT 발표자 노트 읽어 주기), 제목 화면·이름 띠, 유튜브 챕터 |
| 내보내기 | 그대로·릴스·쇼츠·유튜브·카카오톡·용량 맞추기·소리만·표지 그림, 고른 구간만, 내보내기 전 점검(없는 원본·빠른 자막·화면 밖 글자·세로 영상의 가려지는 곳 등) |

얼굴·목소리·화면은 이 PC 안에서만 다루고 어디로도 보내지 않습니다. 자동 자막도 이 PC에서 받아쓰며, 받아쓰기 모델(OpenAI Whisper, 빠르게 약 74MB·정확하게 약 1.0GB)은 처음 쓸 때 물어본 뒤 받습니다. 휴대폰으로 찍은 영상의 촬영 위치 정보는 결과 파일에 남기지 않습니다. 하던 편집은 자동으로 저장되어 다음에 같은 영상을 넣으면 이어서 할 수 있습니다.

PDF·한글·워드 → PPT는 글자를 글상자로 옮기고 표의 선과 그림은 슬라이드 배경으로 둡니다. 스캔본은 글자 인식으로 읽으므로 틀린 글자가 있을 수 있습니다.

결과는 알림 창의 [전후 비교]로 원본과 나란히 볼 수 있고(용량 줄이기·사진 손질·워터마크·쪽 번호), 설정 → 메뉴 칸에서 칸을 끄거나 차례를 바꿀 수 있습니다.

### 필요한 프로그램

- 윈도우 10 또는 11 (.NET Framework 4.8, 윈도우에 기본으로 들어 있음)
- 한글 파일: 한컴오피스 한글
- 워드 파일: MS 워드 (한컴 한워드는 자동 변환을 지원하지 않아요)
- 엑셀·PPT 파일: MS 오피스, 또는 한컴오피스 한셀·한쇼

### AI 연결 (선택)

설정 → AI 연결에서 하나를 고릅니다.

| 연결 | 쓰는 것 |
|---|---|
| Claude | 선생님의 Claude Pro·Max 구독. 이 PC에 설치한 Claude Code에 로그인해 두면 이어 씁니다. |
| ChatGPT | 선생님의 ChatGPT Plus·Pro 요금제. OpenAI의 'Sign in with ChatGPT'로 로그인합니다. |
| Gemini | Google AI Studio에서 만든 API 키 |
| OpenRouter | OpenRouter 계정의 무료 AI 모델 |

키와 로그인 정보는 윈도우 사용자 암호화(DPAPI)로 이 PC의 내 계정에만 저장합니다.

Gemini의 [키 받으러 가기]를 누르면 최대 10분 동안, 복사한 글이 바뀔 때만 키 모양인지 확인합니다. 키 모양이 아닌 글은 저장하거나 보내지 않고, 넣은 키는 복사해 둔 글에서 지웁니다. 구글 로그인 정보는 바꿈에 들어오지 않습니다.

## 설치와 지우기

배포 ZIP을 풀고 `바꿈 설치.exe`를 실행해 [설치]를 누릅니다. 관리자 권한 없이 내 계정에만 설치합니다.

- 프로그램: `%LOCALAPPDATA%\Programs\Bakkum`
- 설정·기록: `%APPDATA%\Bakkum` (기록에는 파일 이름을 남기지 않습니다)
- 시작 메뉴·바탕 화면·보내기 바로 가기, 윈도우 시작 시 켜기(시작 프로그램 폴더의 바로 가기)
- 앱 목록 등록은 설치된 프로그램이 처음 켜질 때 합니다.

지우기는 설정 → 그 밖에 → [프로그램 지우기] 또는 윈도우 설정 → 앱에서 합니다. 위 항목을 모두 지우고, ChatGPT에 로그인해 두었다면 OpenAI 쪽 연결도 끊습니다. 변환 결과 파일은 지우지 않습니다.

## 같이 만들기

고치거나 기능을 넣으려면 [협업_안내.md](협업_안내.md)와 [CLAUDE.md](CLAUDE.md)(Claude Code가 읽는 작업 규칙)를 먼저 읽어 주세요. main에는 PR로만 넣고 상대의 승인을 받습니다.

## 빌드

Visual Studio나 SDK 없이 윈도우에 들어 있는 C# 컴파일러(`csc.exe`, C# 5)로 만듭니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1        # build\Bakkum.exe
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Zip   # dist\바꿈-<버전>.zip 까지
```

C# 5라서 `$""`, `?.`, `nameof`, `=>` 멤버 같은 새 문법은 쓰지 않습니다.

## 새 버전 올리기

`src/Common.cs`의 `Version`을 올리고 `build.ps1 -Zip`을 돌리면 `dist`에 세 가지가 생깁니다.

1. `바꿈-<버전>.zip`: 처음 설치하는 사람용
2. `Bakkum.exe`: GitHub Releases의 `v<버전>` 태그에 먼저 올립니다.
3. `version.json`: `notes`에 고친 점을 적어 `https://hirame-ki.github.io/bakkum/version.json`에 올립니다. 이 파일이 올라가면 사용자에게 알림이 가므로 맨 마지막에 올립니다.

프로그램은 저장소와 사이트의 주소만 받고, 받은 파일의 SHA-256이 `version.json`과 맞을 때만 실행합니다.

## 시험

시험은 프로그램 창을 띄우지 않고 Bakkum.exe를 켜지도 않습니다. `-Dir`에는 시험 전용 빈 폴더를 줍니다. 묶음 목록과 필요한 재료는 `tools\run_tests.ps1` 맨 위에 적혀 있습니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\run_tests.ps1 -Dir C:\bakkum-test               # 기본 묶음(재료 없이)
powershell -ExecutionPolicy Bypass -File .\tools\run_tests.ps1 -Dir C:\bakkum-test -Suites net   # AI 연결
```

AI 연결은 이 PC 안의 가짜 서버(`tools\mock_ai.py`, 가짜 Claude Code `tools\mock_claude.py`)로 시험합니다. 진짜 서비스에는 연결하지 않습니다. 파이썬 3과 cryptography 패키지가 필요합니다.

화면 없이 변환만 해 볼 수도 있습니다.

```powershell
build\Bakkum.exe /run pdf go 문서.hwp
build\Bakkum.exe /run image png 문서.pdf
build\Bakkum.exe /run shrink medium 사진.jpg 문서.pdf
build\Bakkum.exe /run text local 스캔.pdf
```

## 소스 구성

| 파일 | 하는 일 |
|---|---|
| `src/App.cs` | 시작, 트레이, 명령줄 |
| `src/DragTrigger.cs`, `src/MenuWindow.cs` | 끌기 중 키 감지, 메뉴 |
| `src/Actions.cs`, `src/Conversions.cs`, `src/Jobs.cs` | 칸 정의, 변환, 작업 줄과 알림 |
| `src/Office.cs` | 한컴오피스·MS 오피스 자동화 |
| `src/PdfFile.cs`, `src/Imaging.cs`, `src/WinRt.cs` | PDF 읽기·쓰기·줄이기, 그림, 윈도우 PDF·글자 인식 |
| `src/Ai.cs`, `src/ChatGpt.cs`, `src/OpenRouter.cs`, `src/Web.cs` | AI 연결 |
| `src/Settings.cs`, `src/SettingsWindow.cs`, `src/Dialogs.cs`, `src/Theme.cs` | 설정과 화면 |
| `src/PhotoEditor.cs`, `src/PhotoOps.cs`, `src/PhotoJobs.cs`, `src/FaceFinder.cs`, `src/WebpLossless.cs` | 사진 손질 |
| `src/PdfTools.cs`, `src/PdfCrypt.cs`, `src/PdfText.cs`, `src/PdfPagesWindow.cs`, `src/PdfJobs.cs` | PDF 편집 |
| `src/DocConvert.cs`, `src/Ooxml.cs`, `src/Workbench.cs` | 문서 바꾸기, 되묻는 창 |
| `src/OpenDoc.cs`, `src/TextPdf.cs`, `src/Epub.cs`, `src/Svg.cs`, `src/Archive.cs` | ODS·ODT, 글자 → PDF, 전자책, SVG, 압축 |
| `src/PptDeck.cs`, `src/PdfToPpt.cs`, `src/DeckDesign.cs` | PPT 쓰기, PDF → 글자 고칠 수 있는 PPT, AI 수업 PPT 디자인 |
| `src/Media.cs`, `src/MediaFoundation.cs`, `src/Mkv.cs`, `src/Gif.cs` | 영상·소리 |
| `src/VideoEditor.cs`, `src/VideoEditorPanels.cs`, `src/VideoTimeline.cs`, `src/VideoPreview.cs` | 영상 편집 창(칸 메뉴·오른쪽 칸·시간 막대·미리 보기) |
| `src/VideoModel.cs`, `src/VideoDecode.cs`, `src/VideoRender.cs`, `src/VideoOverlay.cs`, `src/VideoAudio.cs`, `src/VideoExport.cs` | 영상 편집 저장 형식, 장면 꺼내기, 그리기, 소리, 내보내기와 점검 |
| `src/VideoTeacher.cs` | 영상 편집의 가리기 따라가기·얼굴 찾기·조용한 곳·화면 녹화 다듬기·수업 자료·제목 화면·챕터 |
| `src/VideoAutoSub.cs`, `src/Whisper.cs`, `src/OnnxPatch.cs` | 자동 자막(받아쓰기 모델 받기·확인·이 PC에서 돌리기) |
| `src/VideoProxy.cs`, `src/VideoEditorProxy.cs` | 미리 보기용 가벼운 사본 |
| `src/ScreenRecorder.cs`, `src/RecordWindows.cs`, `src/Webcam.cs` | 화면 녹화, 웹캠 같이 찍기 |
| `src/AiTasks.cs` | 'AI로' 칸(이름 가리기·보내기 전 확인·작업별 지시) |
| `src/AiOrganize.cs` | 'AI로' 칸의 폴더 정리(파일 이름 목록 보내기·확인 창·옮기기) |
| `src/Compare.cs`, `src/Guide.cs`, `src/Favorites.cs`, `src/Updater.cs` | 전후 비교, 사용법, 즐겨찾기, 앱 안 업데이트 |
| `src/Installer.cs` | 설치·지우기·앱 목록 등록 |
| `src/SelfTest.cs`, `src/ToolTest*.cs`, `tools/` | 자체 시험과 가짜 서버 |

## 라이선스

GNU General Public License 3.0(GPL-3.0)과 추가 조건. 전문은 [LICENSE](LICENSE)에 있습니다. 누구나 쓰고, 나누고, 고칠 수 있지만, 고친 판을 나누거나 팔 때는 그 판의 소스 전체도 같은 조건으로 공개해야 합니다.

GPL-3.0 제7조에 따른 추가 조건(자세한 문구는 [NOTICE.txt](NOTICE.txt)):

- 저작권 표시와 만든 사람 표시를 지우지 않습니다.
- 고친 판은 원본과 다르다고 밝히고, 원작자가 만든 것처럼 보이게 하지 않습니다.
- '바꿈'이라는 이름과 아이콘·로고에 대한 상표 권리는 주지 않습니다. 고친 판에는 다른 이름과 아이콘을 씁니다.
- 만든 사람의 이름을 고친 판이나 다른 제품 홍보에 쓰지 않습니다.

0.3.4까지는 Apache License 2.0으로 배포했고, 그 버전들에는 그 조건이 그대로 적용됩니다. 함께 쓰는 프로그램·서비스와 상표 안내도 [NOTICE.txt](NOTICE.txt)에 있습니다.

바꿈은 한글과컴퓨터, Microsoft, OpenAI, Anthropic, Google, OpenRouter와 관계가 없고, 이 회사들이 후원하거나 보증하지 않습니다.
