// Interop harness module. Kept separate from the gateway/shared modules so a Go
// client can be built here without adding a test-only dependency to any module
// that ships to production.
module github.com/duycuong/rpg-mmo/gameserver-dotnet/interop/kcpprobe

go 1.26.0

require github.com/duycuong/rpg-mmo/shared v0.0.0

require (
	github.com/klauspost/cpuid/v2 v2.2.10 // indirect
	github.com/klauspost/reedsolomon v1.12.0 // indirect
	github.com/pkg/errors v0.9.1 // indirect
	github.com/tjfoc/gmsm v1.4.1 // indirect
	github.com/xtaci/kcp-go/v5 v5.6.72 // indirect
	golang.org/x/crypto v0.45.0 // indirect
	golang.org/x/net v0.47.0 // indirect
	golang.org/x/sys v0.38.0 // indirect
	golang.org/x/time v0.14.0 // indirect
	google.golang.org/protobuf v1.36.6 // indirect
)

replace github.com/duycuong/rpg-mmo/shared => ../../../shared
