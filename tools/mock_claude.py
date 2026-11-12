# SPDX-License-Identifier: GPL-3.0-only
# Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
"""바꿈 자체 시험용 가짜 Claude Code. 어떤 서버에도 연결하지 않는다.

Bakkum.exe /selftest-net 이 작은 .cmd 파일을 거쳐 이 스크립트를 부른다(npm으로 깐 claude.cmd와 같은 길).
- MOCK_CLAUDE_MODE: 어떻게 굴지(아래 main 참고)
- MOCK_CLAUDE_REPORT: 받은 요청에서 틀린 점을 한 줄에 하나씩(JSON) 덧붙여 적는 파일
명령줄·작업 폴더·환경 변수·표준 입력이 바꿈이 보내기로 한 모양(공식 문서의 stream-json)과 맞는지 본다.
"""
import base64
import json
import os
import re
import sys
import time

# SelfTest.cs와 글자까지 같아야 한다
SYSTEM = "You are a test step: copy the page's text, nothing else."
PROMPT = "이 쪽의 글자를 그대로 옮겨 주세요."
PICTURES = [
    bytes([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0xFF, 0xD9]),
    bytes([0xFF, 0xD8, 0xFF, 0xE0, 5, 6, 7, 8, 0xFF, 0xD9]),
]
ANSWER = "안녕하세요"

# 값이 따라오는 옵션과 그 값, 값 없는 옵션. 이 밖의 인자가 오면 틀린 것으로 적는다.
PAIRS = {
    "--input-format": "stream-json",
    "--output-format": "stream-json",
    "--tools": "",
    "--disallowedTools": "*",
    "--permission-prompts": "none",
    "--model": "sonnet",
    "--system-prompt": SYSTEM,
}
SINGLES = {
    "-p", "--verbose", "--include-partial-messages", "--strict-mcp-config", "--restricted",
    "--no-session-persistence", "--disable-slash-commands",
}

INIT = {
    "type": "system", "subtype": "init", "session_id": "mock-session", "model": "claude-sonnet-mock",
    "permissionMode": "default", "tools": [], "mcp_servers": [], "plugins": [], "slash_commands": [],
}


def report(**item):
    path = os.environ.get("MOCK_CLAUDE_REPORT")
    if path:
        with open(path, "a", encoding="utf-8") as f:
            f.write(json.dumps(item, ensure_ascii=False) + "\n")


def fail(message):
    report(fail=message)


def out(obj):
    sys.stdout.buffer.write((json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8"))
    sys.stdout.buffer.flush()


def check_flags(args):
    seen = {}
    i = 0
    while i < len(args):
        a = args[i]
        if a in PAIRS:
            if i + 1 >= len(args):
                fail("value missing after " + a)
                break
            seen[a] = args[i + 1]
            i += 2
        elif a in SINGLES:
            seen[a] = True
            i += 1
        else:
            fail("unexpected argument: " + repr(a))
            i += 1
    for k, v in PAIRS.items():
        if k not in seen:
            fail("missing " + k)
        elif seen[k] != v:
            fail("wrong value for %s: %r" % (k, seen[k]))
    for k in sorted(SINGLES):
        if k not in seen:
            fail("missing " + k)


def check_input(raw):
    if raw.startswith(b"\xef\xbb\xbf"):
        fail("stdin starts with a UTF-8 BOM")
        raw = raw[3:]
    lines = [l for l in raw.split(b"\n") if l.strip()]
    if len(lines) != 1:
        fail("stdin has %d lines, expected 1" % len(lines))
        if not lines:
            return
    try:
        msg = json.loads(lines[0].decode("ascii"))
    except Exception as e:
        fail("stdin line is not ASCII JSON (%s)" % type(e).__name__)
        return
    if msg.get("type") != "user":
        fail("type is not user")
    if "parent_tool_use_id" not in msg or msg["parent_tool_use_id"] is not None:
        fail("parent_tool_use_id is not null")
    m = msg.get("message") or {}
    if m.get("role") != "user":
        fail("message.role is not user")
    content = m.get("content")
    if not isinstance(content, list) or len(content) != len(PICTURES) + 1:
        fail("content is not %d blocks" % (len(PICTURES) + 1))
        return
    for i, pic in enumerate(PICTURES):
        block = content[i] if isinstance(content[i], dict) else {}
        src = block.get("source") or {}
        if block.get("type") != "image" or src.get("type") != "base64" or src.get("media_type") != "image/jpeg":
            fail("image block %d has the wrong shape" % i)
        elif base64.b64decode(src.get("data", "")) != pic:
            fail("image block %d has the wrong bytes" % i)
    last = content[-1] if isinstance(content[-1], dict) else {}
    if last.get("type") != "text" or last.get("text") != PROMPT:
        fail("last block is not the question text")


def text_events(text):
    out({"type": "stream_event", "event": {"type": "message_start", "message": {"role": "assistant", "content": []}}})
    out({"type": "stream_event", "event": {"type": "content_block_start", "index": 0, "content_block": {"type": "text", "text": ""}}})
    for part in (text[:2], text[2:]):
        out({"type": "stream_event", "event": {"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": part}}})
    out({"type": "stream_event", "event": {"type": "content_block_stop", "index": 0}})
    out({"type": "assistant", "message": {"role": "assistant", "content": [{"type": "text", "text": text}]}})


def result(text, is_error=False, denials=()):
    out({"type": "result", "subtype": "success", "is_error": is_error, "result": text,
         "num_turns": 1, "session_id": "mock-session", "permission_denials": list(denials)})


def status(args, mode):
    if args != ["auth", "status", "--json"]:
        fail("status arguments: " + repr(args))
    if mode == "status_garbage":
        sys.stderr.write("error: unknown command 'auth'\n")
        return 1
    logged_in, method, code = {
        "status_sub": (True, "claude.ai", 0),
        "status_key": (True, "api_key", 0),
    }.get("status_sub" if mode.startswith("tasks") else mode, (False, "none", 1))
    out({"loggedIn": logged_in, "authMethod": method, "apiProvider": "firstParty"})
    return code


# ── 'AI로' 칸 시험(tasks 모드): 지시문에 맞는 정해진 답을 돌려주고, 받은 글을 적어 둔다 ──

TOKEN = re.compile(r"\[\[[NPER]\d+\]\]")

SCHEDULE = [
    {"title": "겨울 캠프 %s", "start": "2026-12-01", "end": "2026-12-03", "time": None, "endTime": None, "place": None, "note": "준비물, 침낭"},
    {"title": "학부모 상담", "start": "2026-12-10", "end": None, "time": "14:30", "endTime": None, "place": "교실, 3층", "note": None},
    {"title": "날짜가 틀린 일정", "start": "언젠가"},
]

# 폴더 정리: 이름에 든 낱말로 나눌 폴더. 마지막 것은 고른 폴더 밖으로 나가려는 답.
ORGANIZE = [("통신문", "가정통신문"), ("수업", "수업 자료"), ("상담", "상담 기록"), ("장치", "NUL"), ("탈출", "..\\..\\밖")]


def organize(ask, doc):
    items = []
    for line in doc.split("\n"):
        cells = line.split("\t")
        if len(cells) != 3:
            continue
        stem = cells[1].rsplit(".", 1)[0]
        item = {"n": int(cells[0])}
        if "folder (" in ask:
            item["folder"] = next((f for k, f in ORGANIZE if k in stem), "")
        if "name (" in ask:
            item["name"] = stem.replace(" (1)", "").replace(" 최종", "") + (" 기록" if "상담" in stem else "")
        items.append(item)
    items.append({"n": 99, "folder": "없는 파일", "name": "없음"})
    return json.dumps(items, ensure_ascii=False)


def canned(prompt):
    a, b = prompt.find("<document>\n"), prompt.rfind("\n</document>")
    doc = prompt[a + 11:b] if 0 <= a < b else ""
    ask = prompt[:a] if a >= 0 else prompt
    found = list(dict.fromkeys(TOKEN.findall(doc)))
    tokens = " ".join(found)
    first = found[0] if found else "이름 없음"
    if "Transcribe all text" in ask:
        page = re.search(r"page (\d+)", ask)
        return "<!-- p. %s -->\n\n# 읽은 제목\n\n읽은 본문 글자" % (page.group(1) if page else "1")
    if "official reply" in ask:
        return "수신: 가람교육지원청\n제목: 안내 회신\n1. 관련: 가람교육지원청 과학-123(2026. 11. 3.)\n2. 위 호와 관련하여 다음과 같이 회신합니다.\n가. 참가 인원: [인원]\n1) 담당: %s\n가) 세부 항목\n(1) 더 깊은 항목\n(가) 가장 깊은 항목\n붙임  1. 명단 1부.  끝.\n2026. 11. 9.\n한빛초등학교" % first
    if "Draft a school plan" in ask:
        return "# 겨울 행사 계획\n\n## 1. 목적\n가. 목적 %s\n\n## 3. 세부 계획\n\n가. 일시: [일시]\n\n## 4. 예산\n\n| 항목 | 금액 |\n| --- | --- |\n| 간식 | [예산] |" % tokens
    if "Draft a results report" in ask:
        return "# 겨울 행사 결과 보고\n\n## 2. 추진 내용\n\n가. 내용 %s\n\n## 3. 성과\n\n가. 참가 [참가 인원]" % tokens
    if "letter to parents" in ask:
        return "# 겨울 행사 안내\n안녕하세요. %s\n1. 일시: 12월 1일\n2026. 11. 9.\n한빛초등학교장\n------ 자르는 선 ------\n## 회신서\n| 학년 | 이름 |\n| --- | --- |\n|  |  |" % tokens
    if "three reading levels" in ask:
        return "# 겨울 수준별 자료\n\n## 기초\n\n쉬운 글 %s\n\n### 확인 문제\n\n1. 질문\n\n## 보통\n\n보통 글\n\n## 심화\n\n깊은 글\n\n## 교사용 답안\n\n- 답" % tokens
    if "Korean lesson plan" in ask:
        return "# 겨울 수업 지도안\n\n| 항목 | 내용 |\n| --- | --- |\n| 차시 | [차시] |\n\n## 수업 흐름\n\n| 단계 | 교사 활동 | 학생 활동 | 시간 | 자료 및 유의점 |\n| --- | --- | --- | --- | --- |\n| 도입 | %s 안내 그리고 오늘 배울 내용을 칠판에 적으며 차례대로 자세하게 설명한다 | 듣기 | 5분 | 없음 |" % first
    if "scoring rubric" in ask:
        return "# 발표 채점 기준표\n\n평가 과제: 발표\n\n| 평가 요소 | 배점 | 상 | 중 | 하 |\n| --- | --- | --- | --- | --- |\n| 내용 %s | 10 | 좋음 (10점) | 보통 (7점) | 부족 (4점) |\n\n총점: 10점" % first
    if "checks the same skills" in ask:
        if "문제없음" in doc:
            return "NONE"
        return "# 겨울 비슷한 문제\n\n1. 새 문제 %s\n\n① 하나\n② 둘\n\n2. 다음 문제\n\n① 셋\n② 넷\n\n## 정답과 해설\n\n1. ② - 까닭\n2. ① - 까닭" % tokens
    if "every action that a teacher" in ask:
        if "할일없음" in doc:
            return "[]"
        if "둘째공문" in doc:
            return json.dumps([
                {"deadline": "2026-12-20", "time": None, "task": "나중에 낼 것 " + first, "submit": "보고서", "to": "교육청 공문"},
                {"deadline": "언젠가", "time": None, "task": "날짜를 못 읽은 일", "submit": None, "to": None},
            ], ensure_ascii=False)
        return "```json\n" + json.dumps([
            {"deadline": None, "time": None, "task": "마감 없는 일", "submit": None, "to": None},
            {"deadline": "2026-12-01", "time": "14:00", "task": "먼저 낼 것", "submit": "동의서", "to": "교무실"},
            {"deadline": "2026-12-01", "time": "09:00", "task": "그날 아침 일", "submit": None, "to": None},
            {"deadline": "2026-12-30", "time": None, "task": " "},
        ], ensure_ascii=False) + "\n```"
    if "exactly three lines" in ask:
        return "첫째 줄 %s\n둘째 줄 *요약*\n셋째 줄 요약" % tokens
    if "fits on one A4" in ask:
        return "```markdown\n# 요약 제목\n\n무엇에 관한 글인지 %s\n\n## 첫 항목\n\n- 항목 *기울임* `코드`\n```" % tokens
    if "busy teacher" in ask:
        return "# 공문 제목\n\n## 한 줄 요약\n\n요약 %s\n\n## 마감일\n\n- 12월 1일\n\n## 제출할 것\n\n- 없음\n\n## 해야 할 일\n\n- 확인\n\n## 참고\n\n- 없음" % tokens
    if "dated event" in ask:
        items = json.loads(json.dumps(SCHEDULE))
        items[0]["title"] = items[0]["title"] % first
        return "```json\n" + json.dumps(items, ensure_ascii=False) + "\n```"
    if ask.startswith("Translate this document"):
        return "번역: " + doc
    if ask.startswith("Rewrite this document in plain"):
        return "쉬운 말: " + doc
    if ask.startswith("Correct only spelling"):
        return "안녕[-하세여-]{+하세요+} 문장 %s\n둘째 줄 [-됬다-]{+됐다+}" % tokens
    if ask.startswith("Find every table"):
        if "표없음" in doc:
            return "NONE"
        return "## 명단\n\n| 이름 | 번호 |\n| --- | --- |\n| %s | 007 |\n| 박민수 | 2 |\n\n## 둘째 표\n\n| 가 | 나 |\n| --- | --- |\n| 1 | 2 |" % first
    if "multiple-choice questions" in ask and "about half five-option" not in ask:
        return "# 문제\n\n1. 첫 문제 %s\n\n① 하나\n② 둘\n③ 셋\n④ 넷\n⑤ 다섯\n\n2. 둘째 문제\n\n① 여섯\n② 일곱\n\n## 정답과 해설\n\n1. ③ - 까닭" % tokens
    if "true-or-false statements" in ask:
        return "# OX 문제\n\n1. 참인 문장 ( O / X )\n\n## 정답과 해설\n\n1. O - 까닭"
    if "short-answer or essay" in ask:
        return "# 서술형 문제\n\n1. 질문 (5점)\n\n## 예시 답안과 채점 기준\n\n1. 예시 답안: 답 / 채점 기준: 기준"
    if "about half five-option" in ask:
        return "# 섞은 문제\n\n1. 질문\n\n① 하나\n② 둘\n\n## 정답과 해설\n\n1. ① - 까닭"
    if "student worksheet" in ask:
        return "# 겨울 학습지\n\n학년 반 번호 이름: ______\n\n## 학습 목표\n\n- 목표\n\n## 교사용 답안\n\n- 답"
    if "slide outline" in ask:
        return json.dumps({"title": "발표 제목", "subtitle": "부제 " + first, "slides": [
            {"title": "첫 장", "bullets": ["항목 하나", "항목 둘"]}, {"title": "둘째 장", "bullets": ["항목 셋"]}]}, ensure_ascii=False)
    if "one folder on a Korean teacher" in ask:
        return organize(ask, doc)
    if "file name" in ask:
        return "새 이름: 제안?.txt"
    return "모르는 지시문"


def previous_calls():
    path = os.environ.get("MOCK_CLAUDE_REPORT")
    if not path or not os.path.exists(path):
        return 0
    with open(path, encoding="utf-8") as f:
        return sum(1 for line in f if '"prompt"' in line)


def run_tasks(args, mode):
    raw = sys.stdin.buffer.read()
    msg = json.loads(raw.split(b"\n")[0].decode("ascii"))
    content = msg["message"]["content"]
    prompt = [b["text"] for b in content if b.get("type") == "text"][-1]
    pictures = sum(1 for b in content if b.get("type") == "image")
    system = args[args.index("--system-prompt") + 1] if "--system-prompt" in args else ""
    calls = previous_calls()
    report(prompt=prompt, pictures=pictures, system=system)
    out(INIT)
    if mode == "tasks_limit" or (mode == "tasks_limit2" and calls >= 1):
        result("Claude AI usage limit reached|1762650000", is_error=True)
        return 1
    answer = "일정을 찾아 봤지만 형식을 지키지 않은 답" if mode == "tasks_badjson" and calls == 0 else canned(prompt)
    text_events(answer)
    result(answer)
    return 0


def main():
    args = sys.argv[1:]
    mode = os.environ.get("MOCK_CLAUDE_MODE", "ok")
    if args[:2] == ["auth", "status"]:
        return status(args, mode)

    report(pid=os.getpid(), mode=mode)
    if os.environ.get("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC") != "1":
        fail("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC is not 1")
    if os.listdir(os.getcwd()):
        fail("working folder is not empty")
    if mode.startswith("tasks"):
        return run_tasks(args, mode)
    if mode == "old":
        # 옛 버전: 옵션을 읽다가 바로 끝난다(입력은 읽지 않음)
        sys.stderr.write("error: unknown option '--restricted'\n")
        return 1
    check_flags(args)
    check_input(sys.stdin.buffer.read())

    if mode == "init_tools":
        out(dict(INIT, tools=["Bash", "Read"]))
        time.sleep(60)
        return 0
    out(INIT)
    if mode == "tool_stream":
        out({"type": "stream_event", "event": {"type": "content_block_start", "index": 0,
             "content_block": {"type": "tool_use", "id": "toolu_mock", "name": "Read", "input": {}}}})
        time.sleep(60)
        return 0
    if mode == "tool_msg":
        out({"type": "assistant", "message": {"role": "assistant", "content": [
            {"type": "tool_use", "id": "toolu_mock", "name": "Read", "input": {"file_path": "C:\\mock.txt"}}]}})
        time.sleep(60)
        return 0
    if mode == "hang":
        time.sleep(60)
        return 0
    if mode == "login":
        result("Not logged in \u00b7 Please run /login", is_error=True)
        return 1
    if mode == "limit":
        result("Claude AI usage limit reached|1762650000", is_error=True)
        return 1
    if mode == "limit_new":   # 2.1.322의 문구
        result("You've hit your monthly spend limit · your limit resets Dec 1", is_error=True)
        return 1
    if mode == "limit_session":   # 옛 정규식에 걸리지 않는 꼴
        result("You've hit your session limit · resets 3pm", is_error=True)
        return 1
    if mode == "limit_team":
        result("You've hit your team's shared budget.", is_error=True)
        return 1
    if mode == "too_long":
        result("Prompt is too long", is_error=True)
        return 1
    if mode == "context_full":   # 'limit reached'가 들어 있지만 한도가 아니다
        result("Context limit reached", is_error=True)
        return 1
    text_events(ANSWER)
    if mode == "denied":
        result(ANSWER, denials=[{"tool_name": "Read", "tool_use_id": "toolu_mock", "tool_input": {}}])
        return 0
    result(ANSWER)
    if mode == "slow_exit":
        time.sleep(60)   # 답은 줬는데 끝나지 않는다
    return 0


if __name__ == "__main__":
    sys.exit(main())
