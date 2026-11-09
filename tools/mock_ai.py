"""바꿈 네트워크 시험용 가짜 서버. 이 PC 안(127.0.0.1)에서만 돈다.

OpenRouter(로그인·키 교환·키 정보·모델 목록·읽기)와
ChatGPT(Sign in with ChatGPT 로그인·토큰·서명 키·모델 목록·Responses 스트리밍) 흉내를 낸다.
진짜 서버에는 아무것도 보내지 않는다. 받은 요청의 모양을 확인해서, 틀린 곳을 /test/checks로 알려 준다.

사용(PowerShell, 두 창):
    python tools\\mock_ai.py 8765
    build\\Bakkum.exe /selftest-net 8765
필요한 것: Python 3.9+, cryptography(서명용 RSA 키).
"""
import base64
import hashlib
import json
import re
import sys
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import padding, rsa

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8765
BASE = f'http://127.0.0.1:{PORT}'
ISSUER = BASE + '/auth'
RESOURCE = 'https://api.openai.com/v1'
SCOPES = 'openid profile email offline_access resource.invoke chatgpt.tokens.use.direct'
NO_PLAN = 'openid profile email offline_access resource.invoke'
CLIENT = 'client-selftest'
OR_KEY = 'sk-or-v1-selftest-0123456789abcdef'
FREE_VISION = ['google/gemma-4-31b-it:free', 'nvidia/nemotron-3.5-super-vl:free', 'test/vision-c:free']
# 바꿈 시험(SelfTest.cs)과 글자까지 같아야 한다
GUIDE = '표는 마크다운 표로 옮겨 주세요.'
PROMPT = '이 쪽의 글자를 그대로 옮겨 주세요.'


def b64u(b):
    return base64.urlsafe_b64encode(b).rstrip(b'=').decode()


def b64u_int(n):
    return b64u(n.to_bytes((n.bit_length() + 7) // 8, 'big'))


KEY = rsa.generate_private_key(public_exponent=65537, key_size=2048)
WRONG = rsa.generate_private_key(public_exponent=65537, key_size=2048)


def jwk(private, kid):
    n = private.public_key().public_numbers()
    return {'kty': 'RSA', 'kid': kid, 'use': 'sig', 'alg': 'RS256', 'n': b64u_int(n.n), 'e': b64u_int(n.e)}


# 다른 키를 앞에 둬서, kid로 맞는 키를 고르는지도 본다
JWKS = {'keys': [jwk(WRONG, 'other-key'), jwk(KEY, 'test-key')]}


def jwt(claims, private):
    head = b64u(json.dumps({'alg': 'RS256', 'typ': 'JWT', 'kid': 'test-key'}, separators=(',', ':')).encode())
    body = b64u(json.dumps(claims, separators=(',', ':')).encode())
    sig = private.sign(f'{head}.{body}'.encode(), padding.PKCS1v15(), hashes.SHA256())
    return f'{head}.{body}.{b64u(sig)}'


LOCK = threading.Lock()
CHECKS = []


def fresh():
    return {
        'or_auth': 'ok', 'or_chat': 'ok', 'or_expect_deny': False, 'or_codes': {}, 'or_chat_calls': 0, 'error200_done': None,
        'gpt_auth': 'ok', 'gpt_badsig': False, 'gpt_aud': None, 'gpt_badnonce': False, 'gpt_scope': 'full',
        'gpt_stream': 'ok', 'gpt_refresh': 'ok', 'gpt_codes': {}, 'gen': 0, 'access': None, 'refresh': None,
        'gpt_return_client': 'normal', 'gpt_revoke_url': 'same', 'gpt_revoke': 'ok',
        'host_id': None, 'last_authorize_client': None, 'last_name_hint': None,
        'revoke_calls': 0, 'foreign_revoke_calls': 0, 'oidc_calls': 0, 'refresh_calls': 0,
    }


S = fresh()


def problem(where, what):
    with LOCK:
        CHECKS.append(f'{where}: {what}')


def is_jpeg_data_url(url):
    if not isinstance(url, str) or not url.startswith('data:image/jpeg;base64,'):
        return False
    try:
        return base64.b64decode(url.split(',', 1)[1]).startswith(b'\xff\xd8')
    except Exception:
        return False


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.0'

    def log_message(self, *args):
        pass

    # ── 보내기 도우미 ──

    def send_json(self, code, obj):
        data = json.dumps(obj, ensure_ascii=False).encode('utf-8')
        self.send_response(code)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def redirect(self, url):
        self.send_response(302)
        self.send_header('Location', url)
        self.send_header('Content-Length', '0')
        self.end_headers()

    def read_body(self):
        n = int(self.headers.get('Content-Length') or 0)
        return self.rfile.read(n) if n else b''

    def content_type(self):
        return (self.headers.get('Content-Type') or '').split(';')[0].strip()

    # ── 길 나누기 ──

    def do_GET(self):
        u = urllib.parse.urlsplit(self.path)
        q = dict(urllib.parse.parse_qsl(u.query, keep_blank_values=True))
        p = u.path
        if p == '/test/checks':
            with LOCK:
                return self.send_json(200, {'checks': list(CHECKS)})
        if p == '/test/state':
            with LOCK:
                return self.send_json(200, {k: v for k, v in S.items() if not isinstance(v, dict)})
        if p == '/openrouter/auth':
            return self.or_auth(q)
        if p == '/openrouter/api/v1/key':
            return self.or_key()
        if p == '/openrouter/api/v1/models':
            return self.or_models()
        if p == '/auth/api/accounts/authorize':
            return self.gpt_authorize(q)
        if p == '/auth/.well-known/jwks.json':
            return self.send_json(200, JWKS)
        if p == '/auth/.well-known/openid-configuration':
            with LOCK:
                S['oidc_calls'] += 1
                where_to = S['gpt_revoke_url']
            # 기본은 발급처와 같은 곳의 끝내기 주소. 'foreign'이면 다른 곳(localhost)을 알려 주고,
            # 바꿈이 거기로는 토큰을 보내지 않는지 본다(보내면 이 서버가 /elsewhere/revoke로 받아 잡아낸다).
            end = ISSUER + '/oauth/revoke' if where_to == 'same' else f'http://localhost:{PORT}/elsewhere/revoke'
            return self.send_json(200, {'issuer': ISSUER, 'jwks_uri': ISSUER + '/.well-known/jwks.json', 'revocation_endpoint': end})
        if p == '/openai/v1/models':
            return self.gpt_models()
        self.send_json(404, {'error': 'not found'})

    def do_POST(self):
        p = urllib.parse.urlsplit(self.path).path
        raw = self.read_body()
        if p == '/test/set':
            return self.test_set(raw)
        if p == '/openrouter/api/v1/auth/keys':
            return self.or_keys(raw)
        if p == '/openrouter/api/v1/chat/completions':
            return self.or_chat(raw)
        if p == '/auth/api/accounts/oauth/token':
            return self.gpt_token(raw)
        if p == '/auth/oauth/revoke':
            return self.gpt_revoke(raw)
        if p == '/elsewhere/revoke':
            with LOCK:
                S['foreign_revoke_calls'] += 1
            problem('ChatGPT 로그아웃', '발급처와 다른 곳의 끝내기 주소로 토큰을 보냈음')
            return self.send_json(200, {})
        if p == '/openai/v1/responses':
            return self.gpt_responses(raw)
        self.send_json(404, {'error': 'not found'})

    def test_set(self, raw):
        global S
        d = json.loads(raw or b'{}')
        with LOCK:
            if d.pop('reset', False):
                S = fresh()
            S.update(d)
            S['or_chat_calls'] = 0
            S['error200_done'] = None
        self.send_json(200, {'ok': True})

    # ── OpenRouter ──

    def or_auth(self, q):
        where = 'OpenRouter 로그인 주소'
        cb = q.get('callback_url', '')
        if not re.fullmatch(r'http://localhost:\d+/callback', cb):
            problem(where, f'callback_url={cb!r}')
        if q.get('code_challenge_method') != 'S256':
            problem(where, 'code_challenge_method가 S256이 아님')
        challenge = q.get('code_challenge', '')
        if not re.fullmatch(r'[A-Za-z0-9_-]{43}', challenge):
            problem(where, 'code_challenge 모양')
        state = q.get('state', '')
        if not re.fullmatch(r'[A-Za-z0-9_-]{43}', state):
            problem(where, 'state 모양')
        with LOCK:
            code = f'or-code-{len(S["or_codes"]) + 1}'
            S['or_codes'][code] = challenge
        # 문서: state는 보낸 그대로 돌아온다
        self.redirect(cb + '?' + urllib.parse.urlencode({'code': code, 'state': state}))

    def or_keys(self, raw):
        where = 'OpenRouter 키 교환'
        if self.content_type() != 'application/json':
            problem(where, 'Content-Type이 application/json이 아님')
        try:
            d = json.loads(raw)
        except Exception:
            problem(where, '본문이 JSON이 아님')
            return self.send_json(400, {'error': {'message': 'bad json', 'code': 400}})
        if d.get('code_challenge_method') != 'S256':
            problem(where, 'code_challenge_method')
        with LOCK:
            challenge = S['or_codes'].pop(d.get('code', ''), None)
            mode = S['or_auth']
        ok = challenge is not None and b64u(hashlib.sha256(d.get('code_verifier', '').encode()).digest()) == challenge
        if not ok:
            problem(where, 'PKCE 검증 실패')
        if mode == 'badcode' or not ok:
            return self.send_json(403, {'error': {'message': 'Invalid code or code_verifier', 'code': 403}})
        self.send_json(200, {'key': OR_KEY, 'user_id': 'user_selftest'})

    def or_key(self):
        if self.headers.get('X-Title') != 'Bakkum':
            problem('OpenRouter 키 정보', 'X-Title 머리글')
        if self.headers.get('Authorization') != 'Bearer ' + OR_KEY:
            return self.send_json(401, {'error': {'message': 'No auth credentials found', 'code': 401}})
        self.send_json(200, {'data': {
            'label': 'sk-or-v1-sel...cdef', 'usage': 0, 'limit': None, 'is_free_tier': True,
            'free_model_daily_requests': {'used': 12, 'limit': 50, 'remaining': 38},
        }})

    def or_models(self):
        def m(model_id, inputs, outputs=('text',)):
            return {'id': model_id, 'architecture': {'input_modalities': list(inputs), 'output_modalities': list(outputs)}}
        self.send_json(200, {'data': [
            m('google/gemma-4-31b-it:free', ('text', 'image')),
            m('test/text-only:free', ('text',)),
            m('nvidia/nemotron-3.5-super-vl:free', ('image', 'text')),
            m('openai/gpt-paid-vision', ('text', 'image')),
            m('test/image-maker:free', ('text', 'image'), ('image',)),
            m('test/vision-c:free', ('text', 'image', 'file')),
        ]})

    def or_chat(self, raw):
        where = 'OpenRouter 읽기'
        if self.headers.get('Authorization') != 'Bearer ' + OR_KEY:
            problem(where, 'Authorization 머리글')
            return self.send_json(401, {'error': {'message': 'User not found.', 'code': 401}})
        if self.headers.get('X-Title') != 'Bakkum':
            problem(where, 'X-Title 머리글')
        if self.content_type() != 'application/json':
            problem(where, 'Content-Type이 application/json이 아님')
        try:
            d = json.loads(raw)
        except Exception:
            problem(where, '본문이 JSON이 아님')
            return self.send_json(400, {'error': {'message': 'bad json', 'code': 400}})
        model = d.get('model')
        with LOCK:
            S['or_chat_calls'] += 1
            mode = S['or_chat']
            deny = S['or_expect_deny']
        self.check_or_body(d, deny)
        policy = {'error': {'message': 'No endpoints found matching your data policy (Free model publication). '
                                       'Configure: https://openrouter.ai/settings/privacy', 'code': 404}}
        if mode == 'fallback':
            if model == FREE_VISION[0]:
                return self.send_json(429, {'error': {'message': f'{model} is temporarily rate-limited upstream.', 'code': 429,
                                                      'metadata': {'raw': 'rate limited', 'provider_name': 'Test'}}})
            if model == FREE_VISION[1]:
                return self.send_json(404, policy)
        if mode == 'policy':
            return self.send_json(404, policy)
        if mode == 'daily':
            return self.send_json(429, {'error': {'message': 'Rate limit exceeded: free-models-per-day. '
                                                             'Add 10 credits to unlock 1000 free model requests per day', 'code': 429}})
        if mode == 'unauthorized':
            return self.send_json(401, {'error': {'message': 'User not found.', 'code': 401}})
        if mode == 'error200':
            with LOCK:
                first = S['error200_done'] is None
                S['error200_done'] = True
            if first:
                return self.send_json(200, {'error': {'message': 'Provider returned error', 'code': 502}})
        reason, content = ('length', '부분') if mode == 'length' else ('stop', '모델:' + str(model))
        self.send_json(200, {'id': 'gen-selftest', 'model': model, 'choices': [
            {'index': 0, 'finish_reason': reason, 'message': {'role': 'assistant', 'content': content}}]})

    def check_or_body(self, d, deny):
        where = 'OpenRouter 읽기 본문'
        msgs = d.get('messages')
        if not (isinstance(msgs, list) and len(msgs) == 1 and msgs[0].get('role') == 'user'):
            return problem(where, 'messages는 사용자 글 하나여야 함(시스템 역할 없이)')
        parts = msgs[0].get('content')
        if not isinstance(parts, list) or len(parts) != 4:
            return problem(where, f'content 조각 수 {len(parts) if isinstance(parts, list) else parts!r}')
        if parts[0].get('type') != 'text' or parts[0].get('text') != GUIDE:
            problem(where, '첫 조각은 안내 글이어야 함')
        for im in parts[1:3]:
            url = (im.get('image_url') or {}).get('url') if im.get('type') == 'image_url' else None
            if not is_jpeg_data_url(url):
                problem(where, '그림은 image_url.url = data:image/jpeg;base64,…')
        if parts[3].get('type') != 'text' or parts[3].get('text') != PROMPT:
            problem(where, '마지막 조각은 요청 글이어야 함')
        if d.get('stream') is not False:
            problem(where, 'stream:false여야 함')
        prov = d.get('provider')
        if deny and (not isinstance(prov, dict) or prov.get('data_collection') != 'deny'):
            problem(where, "provider.data_collection='deny'가 없음")
        if not deny and prov is not None:
            problem(where, '스위치를 껐는데 provider를 보냄')

    # ── ChatGPT ──

    def gpt_authorize(self, q):
        where = 'ChatGPT 로그인 주소'
        need = {'response_type': 'code', 'scope': SCOPES, 'resource': RESOURCE, 'code_challenge_method': 'S256'}
        for k, v in need.items():
            if q.get(k) != v:
                problem(where, f'{k}={q.get(k)!r}')
        redirect = q.get('redirect_uri', '')
        if not re.fullmatch(r'http://127\.0\.0\.1:\d+/auth/callback', redirect):
            problem(where, f'redirect_uri={redirect!r}')
        host = q.get('ext_agent_host_id', '')
        if not re.fullmatch(r'urn:uuid:[0-9a-f-]{36}', host):
            problem(where, 'ext_agent_host_id 모양')
        for k in ('state', 'nonce', 'code_challenge'):
            if not re.fullmatch(r'[A-Za-z0-9_-]{43}', q.get(k, '')):
                problem(where, f'{k} 모양')
        client = q.get('client_id')
        hint = q.get('agent_name_hint')
        if client == 'dynamic_agent_client':
            if hint != '바꿈':
                problem(where, f'처음 등록에는 agent_name_hint=바꿈이어야 함(받음 {hint!r})')
        elif client == CLIENT:
            if hint is not None:
                problem(where, '등록된 뒤에는 agent_name_hint를 보내지 않아야 함')
        else:
            problem(where, f'client_id={client!r}')
        with LOCK:
            if S['host_id'] is None:
                S['host_id'] = host
            elif S['host_id'] != host:
                problem(where, 'ext_agent_host_id가 로그인마다 바뀜')
            S['last_authorize_client'] = client
            S['last_name_hint'] = hint
            mode = S['gpt_auth']
            give = S['gpt_return_client']
            code = f'gpt-code-{len(S["gpt_codes"]) + 1}-{int(time.time() * 1000)}'
            S['gpt_codes'][code] = {'nonce': q.get('nonce'), 'challenge': q.get('code_challenge'), 'redirect': redirect}
        if mode == 'deny':
            return self.redirect(redirect + '?' + urllib.parse.urlencode({'error': 'access_denied', 'state': q.get('state', '')}))
        back = {'code': code, 'state': q.get('state', '')}
        # 문서: 새로 등록했을 때만 client_id가 돌아온다. 'none' = 처음인데 안 옴(등록 미완료), 'other' = 다른 client_id가 옴
        if give == 'other':
            back['client_id'] = 'client-other'
        elif give == 'normal' and client == 'dynamic_agent_client':
            back['client_id'] = CLIENT
        back['scope'] = SCOPES
        self.redirect(redirect + '?' + urllib.parse.urlencode(back))

    def gpt_token(self, raw):
        where = 'ChatGPT 토큰'
        if self.content_type() != 'application/x-www-form-urlencoded':
            problem(where, 'Content-Type이 form이 아님')
        f = dict(urllib.parse.parse_qsl(raw.decode('ascii'), keep_blank_values=True))
        if f.get('resource') != RESOURCE:
            problem(where, f'resource={f.get("resource")!r}')
        if f.get('client_id') != CLIENT:
            problem(where, f'client_id={f.get("client_id")!r} (돌려받은 client_id를 써야 함)')
        grant = f.get('grant_type')
        if grant == 'authorization_code':
            with LOCK:
                info = S['gpt_codes'].pop(f.get('code', ''), None)
            if info is None:
                problem(where, '모르는 code')
                return self.send_json(400, {'error': 'invalid_grant'})
            if f.get('redirect_uri') != info['redirect']:
                problem(where, 'redirect_uri가 로그인 때와 다름')
            if b64u(hashlib.sha256(f.get('code_verifier', '').encode()).digest()) != info['challenge']:
                problem(where, 'PKCE 검증 실패')
                return self.send_json(400, {'error': 'invalid_grant'})
            return self.send_json(200, self.issue(info['nonce'], True))
        if grant == 'refresh_token':
            with LOCK:
                S['refresh_calls'] += 1
                mode = S['gpt_refresh']
                current = S['refresh']
            if f.get('refresh_token') != current:
                problem(where, '최신 새로 고침 토큰이 아님')
            if 'scope' in f:
                problem(where, '새로 고침에는 scope를 보내지 않아야 함')
            if mode in ('invalid_grant', 'invalid_refresh_token', 'token_expired', 'refresh_token_expired',
                        'refresh_token_invalidated', 'refresh_token_reused'):
                return self.send_json(400, {'error': mode, 'error_description': 'Sign in again'})
            if mode == 'invalid_client':
                return self.send_json(401, {'error': 'invalid_client'})
            if mode == 'server':
                return self.send_json(500, {'error': 'server_error'})
            return self.send_json(200, self.issue(None, False))
        problem(where, f'grant_type={grant!r}')
        self.send_json(400, {'error': 'unsupported_grant_type'})

    def issue(self, nonce, with_id):
        with LOCK:
            S['gen'] += 1
            g = S['gen']
            S['access'], S['refresh'] = f'at-{g}', f'rt-{g}'
            badsig, aud, badnonce, scope = S['gpt_badsig'], S['gpt_aud'], S['gpt_badnonce'], S['gpt_scope']
        out = {'access_token': f'at-{g}', 'refresh_token': f'rt-{g}', 'token_type': 'Bearer', 'expires_in': 3600,
               'scope': SCOPES if scope == 'full' else NO_PLAN}
        if with_id:
            now = int(time.time())
            claims = {'iss': ISSUER, 'aud': aud or CLIENT, 'sub': 'user-selftest', 'email': 'teacher@example.com',
                      'iat': now, 'exp': now + 3600, 'nonce': 'not-the-nonce' if badnonce else nonce}
            out['id_token'] = jwt(claims, WRONG if badsig else KEY)
        return out

    def gpt_revoke(self, raw):
        where = 'ChatGPT 로그아웃'
        if self.content_type() != 'application/x-www-form-urlencoded':
            problem(where, 'Content-Type이 form이 아님')
        f = dict(urllib.parse.parse_qsl(raw.decode('ascii'), keep_blank_values=True))
        with LOCK:
            S['revoke_calls'] += 1
            current, mode = S['refresh'], S['gpt_revoke']
        if f.get('token') != current:
            problem(where, 'token이 최신 새로 고침 토큰이 아님')
        if f.get('token_type_hint') != 'refresh_token':
            problem(where, f'token_type_hint={f.get("token_type_hint")!r}')
        if f.get('client_id') != CLIENT:
            problem(where, f'client_id={f.get("client_id")!r}')
        if mode == 'fail':
            return self.send_json(503, {'error': 'temporarily_unavailable'})
        with LOCK:
            S['access'], S['refresh'] = None, None
        # 문서: 성공은 빈 200
        self.send_response(200)
        self.send_header('Content-Length', '0')
        self.end_headers()

    def gpt_models(self):
        with LOCK:
            access = S['access']
        if self.headers.get('Authorization') != f'Bearer {access}':
            problem('ChatGPT 모델 목록', 'Authorization이 최신 토큰이 아님')
            return self.send_json(401, {'error': {'code': 'invalid_token', 'message': 'bad token'}})
        self.send_json(200, {'models': [
            {'slug': 'gpt-hidden', 'visibility': 'hide'},
            {'slug': 'gpt-test-1', 'visibility': 'list'},
            {'slug': 'gpt-test-2', 'visibility': 'list'},
        ]})

    def gpt_responses(self, raw):
        where = 'ChatGPT 읽기'
        with LOCK:
            access = S['access']
            mode = S['gpt_stream']
        if self.headers.get('Authorization') != f'Bearer {access}':
            problem(where, 'Authorization이 최신 토큰이 아님')
            return self.send_json(401, {'error': {'code': 'invalid_token', 'message': 'bad token'}})
        if 'text/event-stream' not in (self.headers.get('Accept') or ''):
            problem(where, 'Accept: text/event-stream이 없음')
        if self.content_type() != 'application/json':
            problem(where, 'Content-Type이 application/json이 아님')
        try:
            d = json.loads(raw)
        except Exception:
            problem(where, '본문이 JSON이 아님')
            return self.send_json(400, {'error': {'code': 'bad_request', 'message': 'bad json'}})
        self.check_gpt_body(d)
        if mode == 'unauthorized':
            return self.send_json(401, {'error': {'code': 'invalid_token', 'message': 'Token revoked'}})
        if mode == 'http429':
            return self.send_json(429, {'error': {'code': 'subscription_sharing_usage_limit_exceeded', 'message': 'Usage limit reached'}})
        if mode == 'not_eligible':
            return self.send_json(403, {'error': {'code': 'subscription_sharing_user_not_eligible', 'message': 'Not eligible'}})
        if mode == 'unsupported':
            return self.send_json(400, {'error': {'code': 'subscription_sharing_unsupported_capability', 'param': 'input', 'message': 'Unsupported'}})
        if mode == 'detail503':
            # 문서: 스트림 전에 {"detail": "…"}로 올 때가 있다
            return self.send_json(503, {'detail': 'Service temporarily unavailable'})
        events = {
            'ok': [
                {'type': 'response.created', 'response': {'id': 'resp_1', 'status': 'in_progress'}},
                {'type': 'response.output_text.delta', 'delta': '안녕'},
                {'type': 'response.output_text.delta', 'delta': '하세요'},
                {'type': 'response.output_text.done', 'text': '안녕하세요'},
                {'type': 'response.completed', 'response': {'id': 'resp_1', 'status': 'completed'}},
            ],
            'incomplete': [
                {'type': 'response.output_text.delta', 'delta': '부분'},
                {'type': 'response.incomplete', 'response': {'status': 'incomplete', 'incomplete_details': {'reason': 'max_output_tokens'}}},
            ],
            'limit': [
                {'type': 'error', 'code': 'subscription_sharing_usage_limit_exceeded', 'message': 'Usage limit reached', 'param': None},
            ],
            'failed': [
                {'type': 'response.failed', 'response': {'status': 'failed', 'error': {'code': 'server_error', 'message': 'boom'}}},
            ],
        }.get(mode, [])
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream; charset=utf-8')
        self.send_header('Cache-Control', 'no-cache')
        self.end_headers()
        try:
            for i, e in enumerate(events):
                e = dict(e, sequence_number=i)
                self.wfile.write(f'event: {e["type"]}\ndata: {json.dumps(e, ensure_ascii=False)}\n\n'.encode('utf-8'))
                self.wfile.flush()
                time.sleep(0.02)
            # 끝 이벤트 뒤에도 연결을 잠깐 열어 둔다: 바꿈이 끝 이벤트에서 스스로 멈추는지 본다
            time.sleep(0.5)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass

    def check_gpt_body(self, d):
        where = 'ChatGPT 읽기 본문'
        if d.get('model') != 'gpt-test-1':
            problem(where, f'model={d.get("model")!r} (목록에서 보이는 첫 모델이어야 함)')
        if d.get('store') is not False:
            problem(where, 'store:false여야 함')
        if d.get('stream') is not True:
            problem(where, 'stream:true여야 함')
        # 문서(models-and-inference): 안내 글은 instructions 칸에. role이 system인 항목은 받지 않는다
        if d.get('instructions') != GUIDE:
            problem(where, f'instructions={d.get("instructions")!r} (안내 글이 그대로 있어야 함)')
        for k in ('temperature', 'top_p', 'max_output_tokens', 'metadata', 'user', 'truncation', 'background',
                  'conversation', 'previous_response_id'):
            if k in d:
                problem(where, f'{k}는 보내지 않아야 함(요금제 연결이 받지 않는 칸)')
        inp = d.get('input')
        if not (isinstance(inp, list) and len(inp) == 1 and inp[0].get('role') == 'user'):
            return problem(where, 'input은 사용자 글 하나여야 함(system 역할 없이)')
        parts = inp[0].get('content') or []
        if len(parts) != 3:
            return problem(where, f'content 조각 수 {len(parts)}')
        for im in parts[0:2]:
            if im.get('type') != 'input_image' or not is_jpeg_data_url(im.get('image_url')):
                problem(where, '그림은 input_image + data:image/jpeg;base64,…')
        if parts[2].get('type') != 'input_text' or parts[2].get('text') != PROMPT:
            problem(where, '마지막 조각은 요청 글이어야 함')


if __name__ == '__main__':
    server = ThreadingHTTPServer(('127.0.0.1', PORT), Handler)
    server.daemon_threads = True
    print(f'mock server ready: {BASE}', flush=True)
    server.serve_forever()
