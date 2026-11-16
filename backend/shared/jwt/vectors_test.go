package jwt

import (
	"bytes"
	"encoding/json"
	"flag"
	"os"
	"testing"
	"time"
)

var updateJWTVectors = flag.Bool("update-jwt-vectors", false, "regenerate the shared JWT golden vectors")

const jwtVectorsPath = "../testdata/jwt/vectors.json"

type jwtVectorClaims struct {
	Sub string `json:"sub"`
	Sid string `json:"sid"`
	Jti string `json:"jti"`
	Cid string `json:"cid"`
	Gid string `json:"gid"`
	Exp int64  `json:"exp"`
}

type jwtVector struct {
	Name    string           `json:"name"`
	Rule    string           `json:"rule"`
	Token   string           `json:"token"`
	Secret  *string          `json:"secret,omitempty"`
	Verdict string           `json:"verdict"`
	Claims  *jwtVectorClaims `json:"claims,omitempty"`
}

type jwtVectorFile struct {
	Schema      string      `json:"schema"`
	Secret      string      `json:"secret"`
	Now         int64       `json:"now"`
	SkewSeconds int64       `json:"skew_seconds"`
	Vectors     []jwtVector `json:"vectors"`
}

func TestJWTGoldenVectors(t *testing.T) {
	generated := generateJWTVectors(t)
	want, err := json.MarshalIndent(generated, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	want = append(want, '\n')
	if *updateJWTVectors {
		if err := os.MkdirAll("../testdata/jwt", 0755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(jwtVectorsPath, want, 0644); err != nil {
			t.Fatal(err)
		}
	}
	data, err := os.ReadFile(jwtVectorsPath)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(data, want) {
		t.Fatal("JWT fixture differs from deterministic generator; run go test ./jwt/ -update-jwt-vectors from backend/shared")
	}
	var fixture jwtVectorFile
	if err := json.Unmarshal(data, &fixture); err != nil {
		t.Fatal(err)
	}
	if fixture.Schema != "rpg-mmo.jwt-vectors/1" || fixture.SkewSeconds != int64(ClockSkew/time.Second) {
		t.Fatal("unsupported JWT fixture schema or clock skew")
	}
	t.Logf("shared JWT vectors: %d", len(fixture.Vectors))
	seen := make(map[string]bool)
	for _, vector := range fixture.Vectors {
		if seen[vector.Name] {
			t.Fatalf("duplicate vector name %q", vector.Name)
		}
		seen[vector.Name] = true
		t.Run(vector.Name, func(t *testing.T) {
			secret := fixture.Secret
			if vector.Secret != nil {
				secret = *vector.Secret
			}
			claims, err := verifyAt(vector.Token, secret, time.Unix(fixture.Now, 0))
			verdict := "accept"
			if err != nil {
				verdict = "refuse"
				if claims != (Claims{}) {
					verdict = "refuse-expired"
				}
			}
			if verdict != vector.Verdict {
				t.Fatalf("%s: verdict = %s, want %s; claims = %+v, error = %v", vector.Rule, verdict, vector.Verdict, claims, err)
			}
			if verdict == "accept" {
				got := jwtVectorClaims{claims.UserID, claims.ServerID, claims.Jti, claims.CharacterID, claims.GameID, claims.ExpireAt}
				if vector.Claims == nil || got != *vector.Claims {
					t.Fatalf("claims = %+v, want %+v", got, vector.Claims)
				}
			} else {
				if vector.Claims != nil {
					t.Fatal("refused vectors must not publish trusted claims")
				}
				if verdict == "refuse-expired" && (claims.IsZero() || claims.UserID != "user-1" || claims.ExpireAt != fixture.Now-6) {
					t.Fatalf("expired token must preserve decoded claims for Keyring.Verify: %+v", claims)
				}
			}
		})
	}
}

func TestJWTIssuerContractRoundTrip(t *testing.T) {
	const secret = "issuer-secret"
	cases := []struct {
		name string
		sign func() (string, error)
		gid  string
	}{
		{"Sign", func() (string, error) { return Sign("user-1", secret, time.Hour) }, ""},
		{"SignWithGame absent", func() (string, error) { return SignWithGame("user-1", "gs-1", "char-1", "", secret, time.Hour) }, ""},
		{"SignWithGame default", func() (string, error) { return SignWithGame("user-1", "gs-1", "char-1", "cuvara", secret, time.Hour) }, ""},
		{"SignWithGame non-default", func() (string, error) {
			return SignWithGame("user-1", "ember-gs-1", "char-1", "ember", secret, time.Hour)
		}, "ember"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			token, err := tc.sign()
			if err != nil {
				t.Fatal(err)
			}
			claims, err := Verify(token, secret)
			if err != nil {
				t.Fatal(err)
			}
			if claims.UserID != "user-1" || claims.GameID != tc.gid || claims.IssuedAt <= 0 || claims.IsExpired() {
				t.Fatalf("issuer produced claims outside the contract: %+v", claims)
			}
		})
	}
}
