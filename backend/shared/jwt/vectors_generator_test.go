package jwt

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"strings"
	"testing"
)

// generateJWTVectors deliberately uses raw JSON, independent of the production
// parser and signer. Even malformed claims have correct HMACs, so each refusal
// exercises its named rule rather than failing incidentally at the signature.
func generateJWTVectors(t *testing.T) jwtVectorFile {
	t.Helper()
	fixture := jwtVectorFile{
		Schema: "rpg-mmo.jwt-vectors/1", Secret: "jwt-parity-golden-secret-☃", Now: 1800000000, SkewSeconds: 5,
	}
	const hdr = `{"alg":"HS256","typ":"JWT"}`
	const payload = `{"sub":"user-1","exp":1800003600}`
	encode := func(raw string) string { return base64.RawURLEncoding.EncodeToString([]byte(raw)) }
	signSegments := func(h, p, secret string) string {
		input := h + "." + p
		mac := hmac.New(sha256.New, []byte(secret))
		mac.Write([]byte(input))
		return input + "." + base64.RawURLEncoding.EncodeToString(mac.Sum(nil))
	}
	sign := func(h, p string) string { return signSegments(encode(h), encode(p), fixture.Secret) }
	add := func(name, rule, token, verdict, rawClaims string) {
		v := jwtVector{Name: name, Rule: rule, Token: token, Verdict: verdict}
		if verdict == "accept" {
			v.Claims = &jwtVectorClaims{}
			var fields map[string]json.RawMessage
			if err := json.Unmarshal([]byte(rawClaims), &fields); err != nil {
				t.Fatalf("generate claims for %s: %v", name, err)
			}
			for key, target := range map[string]any{
				"sub": &v.Claims.Sub, "sid": &v.Claims.Sid, "jti": &v.Claims.Jti,
				"cid": &v.Claims.Cid, "gid": &v.Claims.Gid, "exp": &v.Claims.Exp,
			} {
				if raw, present := fields[key]; present {
					if err := json.Unmarshal(raw, target); err != nil {
						t.Fatalf("generate %s claim for %s: %v", key, name, err)
					}
				}
			}
		}
		fixture.Vectors = append(fixture.Vectors, v)
	}
	addPayload := func(name, raw, verdict string) { add(name, "C6", sign(hdr, raw), verdict, raw) }
	withClaim := func(name, value string) string {
		return `{"sub":"user-1","exp":1800003600,"` + name + `":` + value + `}`
	}
	addPayload("valid-gateway-with-cid", withClaim("cid", `"char-1"`), "accept")
	addPayload("valid-join-with-sid-and-jti", `{"sub":"user-1","sid":"gs-1","jti":"join-1","cid":"char-1","iat":1800000000,"exp":1800003600}`, "accept")
	addPayload("valid-non-default-gid", withClaim("gid", `"ember"`), "accept")
	addPayload("gid-absent", payload, "accept")
	for _, tc := range []struct{ name, raw string }{
		{"typ-missing", `{"alg":"HS256"}`},
		{"typ-lowercase", `{"alg":"HS256","typ":"jwt"}`},
		{"typ-jwe", `{"alg":"HS256","typ":"JWE"}`},
		{"typ-null", `{"alg":"HS256","typ":null}`},
		{"typ-number", `{"alg":"HS256","typ":1}`},
		{"typ-uppercase-key", `{"alg":"HS256","TYP":"JWT"}`},
		{"crit-present", `{"alg":"HS256","typ":"JWT","crit":[]}`},
		{"crit-null", `{"alg":"HS256","typ":"JWT","crit":null}`},
		{"alg-none", `{"alg":"none","typ":"JWT"}`},
		{"alg-hs512", `{"alg":"HS512","typ":"JWT"}`},
		{"alg-rs256", `{"alg":"RS256","typ":"JWT"}`},
		{"alg-lowercase", `{"alg":"hs256","typ":"JWT"}`},
		{"alg-missing", `{"typ":"JWT"}`},
		{"alg-non-string", `{"alg":256,"typ":"JWT"}`},
		{"alg-null", `{"alg":null,"typ":"JWT"}`},
		{"alg-uppercase-key", `{"ALG":"HS256","typ":"JWT"}`},
		{"duplicate-alg", `{"alg":"HS256","alg":"HS256","typ":"JWT"}`},
		{"duplicate-typ", `{"alg":"HS256","typ":"JWT","typ":"JWT"}`},
		{"duplicate-escaped-alg", `{"alg":"none","\u0061lg":"HS256","typ":"JWT"}`},
		{"duplicate-unknown-header", `{"alg":"HS256","typ":"JWT","x":1,"x":2}`},
		{"header-array", `[]`}, {"header-null", `null`},
		{"header-invalid-json", `{`},
		{"header-trailing-json", hdr + `{}`},
		{"header-invalid-utf8", "{\"alg\":\"HS256\",\"typ\":\"JWT\",\"x\":\"\xff\"}"},
	} {
		add(tc.name, "C4", sign(tc.raw, payload), "refuse", "")
	}
	add("unknown-header-ignored", "C4", sign(`{"alg":"HS256","typ":"JWT","x":{"ok":true},"CRIT":[]}`, payload), "accept", payload)
	base := sign(hdr, payload)
	parts := strings.Split(base, ".")
	add("token-empty", "C1", "", "refuse", "")
	add("two-segments", "C2", parts[0]+"."+parts[1], "refuse", "")
	add("four-segments", "C2", base+".extra", "refuse", "")
	add("wrong-secret", "C5", base, "refuse", "")
	wrong := "wrong-secret"
	fixture.Vectors[len(fixture.Vectors)-1].Secret = &wrong
	add("empty-secret", "C1", signSegments(parts[0], parts[1], ""), "refuse", "")
	empty := ""
	fixture.Vectors[len(fixture.Vectors)-1].Secret = &empty
	for _, tc := range []struct{ name, h, p string }{
		{"header-padding", parts[0] + "=", parts[1]},
		{"payload-padding", parts[0], parts[1] + "="},
		{"header-newline", parts[0][:4] + "\n" + parts[0][4:], parts[1]},
		{"payload-crlf", parts[0], parts[1][:4] + "\r\n" + parts[1][4:]},
		{"header-space", " " + parts[0], parts[1]},
		{"payload-tab", parts[0], "\t" + parts[1]},
		{"header-invalid-length", "A", parts[1]},
		{"payload-invalid-length", parts[0], "A"},
		{"header-empty", "", parts[1]},
		{"payload-empty", parts[0], ""},
		{"payload-plus", parts[0], "+" + parts[1]},
		{"header-slash", "/" + parts[0], parts[1]},
	} {
		add(tc.name, "C3", signSegments(tc.h, tc.p, fixture.Secret), "refuse", "")
	}
	// Change only unused low bits: permissive decoders produce the same bytes.
	nonCanonical := func(seg string) string {
		const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
		if len(seg)%4 != 2 && len(seg)%4 != 3 {
			t.Fatalf("segment has no unused trailing bits: %s", seg)
		}
		last := strings.IndexByte(alphabet, seg[len(seg)-1])
		return seg[:len(seg)-1] + string(alphabet[last|1])
	}
	add("signature-trailing-bits", "C3", parts[0]+"."+parts[1]+"."+nonCanonical(parts[2]), "refuse", "")
	paddedHeader := encode(hdr + " ")
	for len(paddedHeader)%4 == 0 {
		hdrRaw, err := base64.RawURLEncoding.DecodeString(paddedHeader)
		if err != nil {
			t.Fatal(err)
		}
		paddedHeader = encode(string(hdrRaw) + " ")
	}
	add("header-trailing-bits", "C3", signSegments(nonCanonical(paddedHeader), parts[1], fixture.Secret), "refuse", "")
	paddedPayload := encode(payload + " ")
	for len(paddedPayload)%4 == 0 {
		paddedPayload = encode(payload + "  ")
	}
	add("payload-trailing-bits", "C3", signSegments(parts[0], nonCanonical(paddedPayload), fixture.Secret), "refuse", "")
	add("signature-padding", "C3", base+"=", "refuse", "")
	add("signature-newline", "C3", base+"\n", "refuse", "")
	add("signature-empty", "C3", parts[0]+"."+parts[1]+".", "refuse", "")
	add("signature-31-bytes", "C3", parts[0]+"."+parts[1]+"."+base64.RawURLEncoding.EncodeToString(make([]byte, 31)), "refuse", "")
	add("signature-33-bytes", "C3", parts[0]+"."+parts[1]+"."+base64.RawURLEncoding.EncodeToString(make([]byte, 33)), "refuse", "")
	// Find a deterministic, valid signature containing both URL-only characters.
	for nonce := 0; ; nonce++ {
		raw := withClaim("nonce", fmt.Sprint(nonce))
		token := sign(hdr, raw)
		sig := strings.Split(token, ".")[2]
		if strings.Contains(sig, "-") && strings.Contains(sig, "_") {
			add("signature-plus", "C3", strings.TrimSuffix(token, sig)+strings.ReplaceAll(sig, "-", "+"), "refuse", "")
			add("signature-slash", "C3", strings.TrimSuffix(token, sig)+strings.ReplaceAll(sig, "_", "/"), "refuse", "")
			break
		}
	}
	for _, tc := range []struct{ name, raw string }{
		{"duplicate-sub", `{"sub":"attacker","sub":"user-1","exp":1800003600}`},
		{"duplicate-escaped-sub", `{"sub":"attacker","\u0073ub":"user-1","exp":1800003600}`},
		{"duplicate-unknown-claim", `{"sub":"user-1","exp":1800003600,"x":1,"x":2}`},
		{"sub-uppercase-key", `{"SUB":"user-1","exp":1800003600}`},
		{"sub-missing", `{"exp":1800003600}`},
		{"sub-empty", `{"sub":"","exp":1800003600}`},
		{"sub-number", `{"sub":123,"exp":1800003600}`},
		{"sub-null", `{"sub":null,"exp":1800003600}`},
		{"payload-array", `[]`}, {"payload-null", `null`},
		{"payload-invalid-json", `{`},
		{"payload-trailing-json", payload + `{}`},
		{"payload-invalid-utf8", "{\"sub\":\"user-1\",\"exp\":1800003600,\"x\":\"\xff\"}"},
		{"exp-missing", `{"sub":"user-1"}`},
		{"exp-uppercase-key", `{"sub":"user-1","EXP":1800003600}`},
	} {
		addPayload(tc.name, tc.raw, "refuse")
	}
	for _, key := range []string{"sid", "jti", "cid", "gid"} {
		for _, tc := range []struct{ name, raw string }{
			{"null", "null"}, {"number", "1"}, {"bool", "true"}, {"array", "[]"}, {"object", "{}"},
		} {
			addPayload(key+"-"+tc.name, withClaim(key, tc.raw), "refuse")
		}
		// Empty gid remains accepted for the existing JwtCharacterClaimTests token.
		addPayload(key+"-empty", withClaim(key, `""`), "accept")
	}
	addPayload("gid-explicit-default", withClaim("gid", `"cuvara"`), "accept")
	for _, value := range []string{"Ember", "gs", "map", "dungeon", "other", "a", "foo-bar", "foo_bar", "éclair", "abcdefghijklmnopqrstuvwxy"} {
		addPayload("gid-invalid-"+value, withClaim("gid", `"`+value+`"`), "refuse")
	}
	for _, value := range []string{"ab", "a1", "abcdefghijklmnopqrstuvwx"} {
		addPayload("gid-valid-"+value, withClaim("gid", `"`+value+`"`), "accept")
	}
	for _, tc := range []struct{ name, value string }{
		{"zero", "0"}, {"negative", "-1"}, {"fraction", "1.5"}, {"exponent", "1e9"},
		{"string", `"1800003600"`}, {"overflow", "9223372036854775808"},
		{"underflow", "-9223372036854775809"}, {"null", "null"}, {"bool", "true"},
		{"leading-zero", "01800003600"}, {"plus", "+1800003600"},
	} {
		addPayload("exp-"+tc.name, `{"sub":"user-1","exp":`+tc.value+`}`, "refuse")
	}
	addPayload("exp-skew-boundary", `{"sub":"user-1","exp":1799999995}`, "accept")
	addPayload("exp-skew-exceeded", `{"sub":"user-1","exp":1799999994}`, "refuse-expired")
	addPayload("exp-int64-max", `{"sub":"user-1","exp":9223372036854775807}`, "accept")
	// Expiration must be classified last: invalid iat/nbf still return zero Claims.
	addPayload("expired-and-invalid-iat", `{"sub":"user-1","exp":1799999994,"iat":1800000006}`, "refuse")
	addPayload("expired-and-invalid-nbf", `{"sub":"user-1","exp":1799999994,"nbf":1800000006}`, "refuse")
	for _, key := range []string{"iat", "nbf"} {
		for _, tc := range []struct{ name, value, verdict string }{
			{"zero", "0", "accept"}, {"skew-boundary", "1800000005", "accept"},
			{"future", "1800000006", "refuse"}, {"fraction", "1.5", "refuse"},
			{"exponent", "1e9", "refuse"}, {"string", `"0"`, "refuse"},
			{"null", "null", "refuse"}, {"bool", "false", "refuse"},
			{"overflow", "9223372036854775808", "refuse"},
			{"int64-max", "9223372036854775807", "refuse"},
		} {
			addPayload(key+"-"+tc.name, withClaim(key, tc.value), tc.verdict)
		}
	}
	addPayload("iat-absent", payload, "accept")
	addPayload("iat-negative", withClaim("iat", "-1"), "refuse")
	addPayload("iat-negative-zero", withClaim("iat", "-0"), "accept")
	addPayload("nbf-negative", withClaim("nbf", "-1"), "accept")
	addPayload("nbf-int64-min", withClaim("nbf", "-9223372036854775808"), "accept")
	addPayload("unknown-claims-ignored", `{"sub":"user-1","exp":1800003600,"string":"s","number":1.5,"bool":true,"null":null,"array":[1,"x"],"object":{"ok":true},"IAT":"ignored","NBF":[]}`, "accept")
	addPayload("claim-names-case-sensitive", `{"sub":"user-1","SUB":"ignored","exp":1800003600,"SID":42,"GID":null}`, "accept")
	return fixture
}
