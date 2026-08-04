module github.com/duycuong/rpg-mmo/nakama

go 1.26.5

require (
	github.com/duycuong/rpg-mmo/shared v0.0.0
	github.com/heroiclabs/nakama-common v1.47.0
)

require google.golang.org/protobuf v1.36.11 // indirect

replace github.com/duycuong/rpg-mmo/shared => ../shared
