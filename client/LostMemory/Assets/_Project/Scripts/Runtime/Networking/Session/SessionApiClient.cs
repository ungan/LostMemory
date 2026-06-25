using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using LostMemory.Networking.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace LostMemory.Networking.Session
{
    /// <summary>
    /// LostMemory 백엔드 (싸피 EC2) 의 Sessions/Auth API HTTP 클라이언트.
    ///
    /// 정적 클래스 — 한 번 LoginAsync 후 accessToken 을 내부 캐시. 후속 호출은 자동으로 Bearer 헤더 부착.
    /// 다중 인스턴스 테스트 (Multiplayer Play Mode) 시 인스턴스마다 별도 자격증명 필요하면
    /// RelaySession.AutoLoginId 등 정적 필드를 인스턴스별로 다르게 설정할 것.
    ///
    /// 의존: Newtonsoft.Json (com.unity.nuget.newtonsoft-json)
    /// </summary>
    public static class SessionApiClient
    {
        /// <summary>
        /// 백엔드 베이스 URL.
        /// Editor (Play 모드 포함) → dev (localhost:8080), Build → prod (k14c201.p.ssafy.io).
        /// 런타임에 강제로 다른 URL 쓰려면 외부에서 직접 대입 가능 (정적 필드).
        /// </summary>
#if UNITY_EDITOR
        public static string BaseUrl = "https://k14c201.p.ssafy.io/api";
#else
        public static string BaseUrl = "https://k14c201.p.ssafy.io/api";
#endif

        private static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        private static readonly JsonSerializerSettings jsonSettings = new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Ignore
        };

        public static string AccessToken { get; private set; }
        public static string RefreshToken { get; private set; }
        public static long MyUserId { get; private set; }
        public static string MyNickname { get; private set; }

        public static bool IsLoggedIn => !string.IsNullOrEmpty(AccessToken) && MyUserId != 0;

        // ============================================================
        // 인증
        // ============================================================

        /// <summary>
        /// 회원가입. 가입 직후엔 status=pending 으로 저장되며 토큰은 발급되지 않는다 —
        /// 이메일 인증(/auth/email/verify) 통과 시점에 토큰을 받는다. 호출자는 envelope.error.code 로
        /// 중복(USER_LOGIN_ID_DUPLICATED / USER_EMAIL_DUPLICATED / USER_NICKNAME_DUPLICATED) 등을 분기.
        /// </summary>
        public static async Task<ApiEnvelope<SignupResponseData>> SignupAsync(
            string loginId, string password, string email, string nickname)
        {
            var body = new { loginId, password, email, nickname };
            return await PostAsync<SignupResponseData>("/auth/signup", body, requireAuth: false);
        }

        /// <summary>
        /// 이메일 인증 코드 검증. 성공 시 status=active 전이 + TokenResponse 반환.
        /// 코드 불일치/만료/시도초과는 envelope.error.code 로 분기.
        /// </summary>
        public static async Task<ApiEnvelope<TokenData>> VerifyEmailAsync(string email, string code)
        {
            var body = new { email, code };
            var resp = await PostAsync<TokenData>("/auth/email/verify", body, requireAuth: false);
            if (resp != null && resp.success && resp.data != null)
            {
                AccessToken = resp.data.accessToken;
                RefreshToken = resp.data.refreshToken;
            }
            return resp;
        }

        /// <summary>
        /// 인증 코드 재발송. 쿨다운(60초) · 일일 한도(5회) · 이미 ACTIVE 등 정책은 envelope.error.code 로 통지.
        /// </summary>
        public static async Task<ApiEnvelope<object>> ResendVerificationCodeAsync(string email)
        {
            var body = new { email };
            return await PostAsync<object>("/auth/email/resend", body, requireAuth: false);
        }

        /// <summary>
        /// 로그인. 성공 시 AccessToken/RefreshToken 정적 필드 갱신 + envelope 반환.
        /// PENDING 상태(이메일 미인증)는 envelope.error.code == "AUTH_EMAIL_NOT_VERIFIED" 로 통지.
        /// </summary>
        public static async Task<ApiEnvelope<TokenData>> LoginAsync(string loginId, string password)
        {
            var body = new { loginId, password };
            var resp = await PostAsync<TokenData>("/auth/login", body, requireAuth: false);
            if (resp != null && resp.success && resp.data != null)
            {
                AccessToken = resp.data.accessToken;
                RefreshToken = resp.data.refreshToken;
            }
            else if (resp != null && !resp.success)
            {
                NetLog.Warn("API", $"login 실패: code={resp.error?.code}");
            }
            return resp;
        }

        public static async Task<bool> FetchMyUserIdAsync()
        {
            var resp = await GetAsync<UserMeData>("/users/me");
            if (resp == null || !resp.success || resp.data == null)
            {
                NetLog.Error("API", $"users/me 실패: code={resp?.error?.code}");
                return false;
            }
            MyUserId = resp.data.userId;
            MyNickname = resp.data.nickname;
            return true;
        }

        // ============================================================
        // Sessions
        // ============================================================

        public static async Task<SessionResponseData> CreateSessionAsync(int maxPlayers, string privateCode)
        {
            var body = new { maxPlayers, privateCode };
            var resp = await PostAsync<SessionResponseData>("/sessions", body);
            if (resp == null || !resp.success)
            {
                NetLog.Error("API", $"create session 실패: code={resp?.error?.code}");
                return null;
            }
            return resp.data;
        }

        public static async Task<SessionFindData> FindByCodeAsync(string privateCode)
        {
            var url = $"/sessions/find?code={Uri.EscapeDataString(privateCode)}";
            var resp = await GetAsync<SessionFindData>(url);
            if (resp == null || !resp.success)
            {
                NetLog.Warn("API", $"find session 실패: code={resp?.error?.code}");
                return null;
            }
            return resp.data;
        }

        public static async Task<SessionResponseData> JoinSessionAsync(long sessionId, string privateCode)
        {
            var body = new { privateCode };
            var resp = await PostAsync<SessionResponseData>($"/sessions/{sessionId}/join", body);
            if (resp == null || !resp.success)
            {
                NetLog.Error("API", $"join session 실패: code={resp?.error?.code}");
                return null;
            }
            return resp.data;
        }

        public static async Task DeleteSessionAsync(long sessionId)
        {
            await DeleteAsync($"/sessions/{sessionId}");
        }

        /// <summary>게스트 자발 이탈. 백엔드가 본인 SessionJoin 만 삭제 → 정원 카운트 회복.</summary>
        public static async Task LeaveSessionAsync(long sessionId)
        {
            await PostAsync<object>($"/sessions/{sessionId}/leave", new { });
        }

        // ============================================================
        // Talent — #144 (4 slot: 치명타율/공격속도/방어력/최대체력)
        // ============================================================

        public static async Task<TalentAllocationData> GetMyTalentsAsync()
        {
            var resp = await GetAsync<TalentAllocationData>("/users/me/talents");
            if (resp == null || !resp.success)
            {
                NetLog.Error("API", $"GetMyTalents 실패: code={resp?.error?.code}");
                return null;
            }
            return resp.data;
        }

        public static async Task<TalentAllocationData> SaveTalentsAsync(TalentSaveBody body)
        {
            var resp = await PostAsync<TalentAllocationData>("/users/me/talents/save", body);
            if (resp == null || !resp.success)
            {
                NetLog.Error("API", $"SaveTalents 실패: code={resp?.error?.code}");
                return null;
            }
            return resp.data;
        }

        // ============================================================
        // Weapon — #144
        // ============================================================

        /// <summary>무기 마스터 트리 — 앱 기동 또는 마을 진입 시 1회. 평탄 리스트, parentWeaponId 로 클라가 트리 재구성.</summary>
        public static async Task<WeaponMasterData[]> GetMasterWeaponsAsync()
        {
            var resp = await GetAsync<WeaponMasterData[]>("/master/weapons");
            if (resp == null || !resp.success)
            {
                NetLog.Error("API", $"GetMasterWeapons 실패: code={resp?.error?.code}");
                return null;
            }
            return resp.data;
        }

        /// <summary>본인 인벤토리 — 해금 목록 + 현재 장착 무기 ID. 마을 진입 시 1회.</summary>
        public static async Task<WeaponInventoryData> GetMyWeaponsAsync()
        {
            var resp = await GetAsync<WeaponInventoryData>("/users/me/weapons");
            if (resp == null || !resp.success)
            {
                NetLog.Error("API", $"GetMyWeapons 실패: code={resp?.error?.code}");
                return null;
            }
            return resp.data;
        }

        /// <summary>무기 해금 (파편 차감). cost 는 클라가 계산해서 보냄. 이미 해금이면 idempotent.</summary>
        public static async Task<ApiEnvelope<WeaponUnlockedData>> UnlockWeaponAsync(long weaponId, int consumedShards)
        {
            var body = new { weaponId, consumedShards };
            var resp = await PostAsync<WeaponUnlockedData>("/users/me/weapons/unlock", body);
            if (resp == null || !resp.success)
            {
                NetLog.Warn("API", $"UnlockWeapon 실패: code={resp?.error?.code}");
            }
            return resp;
        }

        /// <summary>장착 무기 갱신. 본인이 해금한 무기만 가능.</summary>
        public static async Task<ApiEnvelope<WeaponSelectionData>> SelectWeaponAsync(long weaponId)
        {
            var body = new { weaponId };
            var resp = await PutAsync<WeaponSelectionData>("/users/me/weapons/selected", body);
            if (resp == null || !resp.success)
            {
                NetLog.Warn("API", $"SelectWeapon 실패: code={resp?.error?.code}");
            }
            return resp;
        }

        // ============================================================
        // Memory — #138/#142 (frame slot 해금)
        // ============================================================

        /// <summary>
        /// 프레임 내 특정 slot 해금 + 파편 차감.
        /// cost 는 클라가 계산해서 보냄. 이미 해금된 slot 이면 idempotent (변화 없음).
        ///
        /// 실패 시 ApiEnvelope 자체 반환 — 호출자가 error.code 분기:
        ///   - 400 MEMORY_SLOT_INDEX_OUT_OF_RANGE — slotIndex 가 0~5 밖
        ///   - 404 MEMORY_FRAME_NOT_FOUND — frameId 존재하지 않음
        ///   - 409 MEMORY_SHARDS_INSUFFICIENT — 보유 파편 부족
        /// </summary>
        public static async Task<ApiEnvelope<MemoryProgressData>> UnlockMemorySlotAsync(long frameId, int slotIndex, int consumedShards)
        {
            var body = new { frameId, slotIndex, consumedShards };
            var resp = await PostAsync<MemoryProgressData>("/memory/progress/unlock-slot", body);
            if (resp == null || !resp.success)
            {
                NetLog.Warn("API", $"UnlockMemorySlot 실패: code={resp?.error?.code}");
            }
            return resp;
        }

        // ============================================================
        // HTTP helpers
        // ============================================================

        private static async Task<ApiEnvelope<T>> PostAsync<T>(string path, object body, bool requireAuth = true)
        {
            var json = JsonConvert.SerializeObject(body, jsonSettings);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path) { Content = content };
            if (requireAuth) AddAuth(req);
            return await SendAndParseAsync<T>(req);
        }

        private static async Task<ApiEnvelope<T>> GetAsync<T>(string path)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            AddAuth(req);
            return await SendAndParseAsync<T>(req);
        }

        private static async Task DeleteAsync(string path)
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, BaseUrl + path);
            AddAuth(req);
            await SendAndParseAsync<object>(req);
        }

        private static async Task<ApiEnvelope<T>> PutAsync<T>(string path, object body)
        {
            var json = JsonConvert.SerializeObject(body, jsonSettings);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Put, BaseUrl + path) { Content = content };
            AddAuth(req);
            return await SendAndParseAsync<T>(req);
        }

        private static void AddAuth(HttpRequestMessage req)
        {
            if (!string.IsNullOrEmpty(AccessToken))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
            }
        }

        private static async Task<ApiEnvelope<T>> SendAndParseAsync<T>(HttpRequestMessage req)
        {
            try
            {
                using var resp = await http.SendAsync(req);
                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(text))
                {
                    return new ApiEnvelope<T> { success = resp.IsSuccessStatusCode };
                }
                return JsonConvert.DeserializeObject<ApiEnvelope<T>>(text, jsonSettings);
            }
            catch (Exception ex)
            {
                NetLog.Error("API", $"{req.Method} {req.RequestUri.AbsolutePath} 실패: {ex.Message}");
                return null;
            }
        }

        // ============================================================
        // 응답 DTO — 백엔드 ApiResponse / 도메인 응답과 1:1 매핑
        // ============================================================

        [Serializable]
        public class ApiEnvelope<T>
        {
            public bool success;
            public T data;
            public ErrorDetail error;
        }

        [Serializable]
        public class ErrorDetail
        {
            public string code;
            public string message;
        }

        [Serializable]
        public class TokenData
        {
            public string accessToken;
            public string refreshToken;
            public long accessTokenExpiresIn;
        }

        /// <summary>회원가입 응답. 가입 직후엔 토큰 미발급 — userId + status("pending") 만 반환된다.</summary>
        [Serializable]
        public class SignupResponseData
        {
            public long userId;
            public string status;
        }

        [Serializable]
        public class UserMeData
        {
            public long userId;
            public string loginId;
            public string nickname;
            public string status;
        }

        [Serializable]
        public class SessionResponseData
        {
            public long sessionId;
            public long hostId;
            public int maxPlayers;
            public string privateCode;
            public string sessionToken;
            public long sessionTokenExpiresIn;
            public SessionMember[] members;
        }

        [Serializable]
        public class SessionMember
        {
            public long userId;
            public string nickname;
            public string role; // "HOST" or "GUEST"
            public string joinedAt;
        }

        [Serializable]
        public class SessionFindData
        {
            public long sessionId;
            public long hostId;
            public int maxPlayers;
            public long currentMembers;
            public bool isFull;
        }

        // ============================================================
        // #144 — Talent / Weapon DTO
        // ============================================================

        [Serializable]
        public class TalentAllocationData
        {
            public long userId;
            public int critRatePoints;
            public int attackSpeedPoints;
            public int defensePoints;
            public int maxHpPoints;
            public string updatedAt;
        }

        [Serializable]
        public class TalentSaveBody
        {
            public int critRatePoints;
            public int attackSpeedPoints;
            public int defensePoints;
            public int maxHpPoints;
        }

        [Serializable]
        public class WeaponMasterData
        {
            public long weaponId;
            public string weaponName;
            public string weaponType;
            public long? parentWeaponId;   // 루트면 null
            public int displayOrder;
        }

        [Serializable]
        public class WeaponInventoryData
        {
            public long selectedWeaponId;
            public WeaponUnlockedData[] unlocks;
        }

        [Serializable]
        public class WeaponUnlockedData
        {
            public long unlockNodeId;
            public string unlockedAt;
        }

        [Serializable]
        public class WeaponSelectionData
        {
            public long selectedWeaponId;
        }

        // ============================================================
        // #138/#142 — Memory DTO
        // ============================================================

        [Serializable]
        public class MemoryProgressData
        {
            public long frameId;
            public int unlockedMask;     // 6-bit. bit n=1 이면 slot n 해금. 63 = 6칸 다 해금
            public string state;         // "Locked" / "In Progress" / "Done" — mask 에서 derive
        }
    }
}
