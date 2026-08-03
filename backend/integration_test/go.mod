module github.com/duycuong/rpg-mmo/integration_test

go 1.24.5

require (
	github.com/duycuong/rpg-mmo/shared v0.0.0
	github.com/duycuong/rpg-mmo/gameserver v0.0.0
	github.com/duycuong/rpg-mmo/gateway v0.0.0
)

replace (
	github.com/duycuong/rpg-mmo/shared => ../shared
	github.com/duycuong/rpg-mmo/gameserver => ../gameserver
	github.com/duycuong/rpg-mmo/gateway => ../gateway
)
