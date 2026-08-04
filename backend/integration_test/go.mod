module github.com/duycuong/rpg-mmo/integration_test

go 1.26.5

require (
	github.com/alicebob/miniredis/v2 v2.38.0
	github.com/duycuong/rpg-mmo/gameserver v0.0.0
	github.com/duycuong/rpg-mmo/gateway v0.0.0
	github.com/duycuong/rpg-mmo/nakama v0.0.0-00010101000000-000000000000
	github.com/duycuong/rpg-mmo/shared v0.0.0
	github.com/redis/go-redis/v9 v9.22.0
)

require (
	agones.dev/agones v1.59.0 // indirect
	github.com/cespare/xxhash/v2 v2.3.0 // indirect
	github.com/grpc-ecosystem/grpc-gateway/v2 v2.27.3 // indirect
	github.com/heroiclabs/nakama-common v1.47.0 // indirect
	github.com/klauspost/cpuid/v2 v2.2.10 // indirect
	github.com/klauspost/reedsolomon v1.12.0 // indirect
	github.com/pkg/errors v0.9.1 // indirect
	github.com/tjfoc/gmsm v1.4.1 // indirect
	github.com/xtaci/kcp-go/v5 v5.6.72 // indirect
	github.com/yuin/gopher-lua v1.1.1 // indirect
	go.uber.org/atomic v1.11.0 // indirect
	golang.org/x/crypto v0.46.0 // indirect
	golang.org/x/net v0.48.0 // indirect
	golang.org/x/sys v0.39.0 // indirect
	golang.org/x/text v0.32.0 // indirect
	golang.org/x/time v0.14.0 // indirect
	google.golang.org/genproto/googleapis/api v0.0.0-20251202230838-ff82c1b0f217 // indirect
	google.golang.org/genproto/googleapis/rpc v0.0.0-20251202230838-ff82c1b0f217 // indirect
	google.golang.org/grpc v1.79.3 // indirect
	google.golang.org/protobuf v1.36.11 // indirect
)

replace (
	github.com/duycuong/rpg-mmo/gameserver => ../gameserver
	github.com/duycuong/rpg-mmo/gateway => ../gateway
	github.com/duycuong/rpg-mmo/shared => ../shared
)

replace github.com/duycuong/rpg-mmo/nakama => ../nakama
