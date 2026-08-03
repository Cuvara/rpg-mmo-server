module github.com/duycuong/rpg-mmo/integration_test

go 1.26

require (
	github.com/duycuong/rpg-mmo/gameserver v0.0.0
	github.com/duycuong/rpg-mmo/gateway v0.0.0
	github.com/duycuong/rpg-mmo/shared v0.0.0
)

require (
	agones.dev/agones v1.59.0 // indirect
	github.com/grpc-ecosystem/grpc-gateway/v2 v2.27.3 // indirect
	github.com/pkg/errors v0.9.1 // indirect
	golang.org/x/net v0.48.0 // indirect
	golang.org/x/sys v0.39.0 // indirect
	golang.org/x/text v0.32.0 // indirect
	google.golang.org/genproto/googleapis/api v0.0.0-20251202230838-ff82c1b0f217 // indirect
	google.golang.org/genproto/googleapis/rpc v0.0.0-20251202230838-ff82c1b0f217 // indirect
	google.golang.org/grpc v1.79.3 // indirect
	google.golang.org/protobuf v1.36.10 // indirect
)

replace (
	github.com/duycuong/rpg-mmo/gameserver => ../gameserver
	github.com/duycuong/rpg-mmo/gateway => ../gateway
	github.com/duycuong/rpg-mmo/shared => ../shared
)
