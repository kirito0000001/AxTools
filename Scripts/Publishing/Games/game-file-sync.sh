#!/bin/bash
# 游戏包「逐文件同步」的服务端半边（2026-09-20 起替代 git 推送）
#
# 部署：scp 到野草云 /srv/scripts/game-file-sync.sh，chmod +x
#
#   game-file-sync.sh prepare  <产品段> <版本> [上一版]   # 从上一版硬链接出这一版，客户端只覆盖变化的文件
#   game-file-sync.sh finalize <产品段> <版本> [上一版]   # 归档上一版 → 清掉清单里没有的文件 → 只留当前版 → 写 latest.json
#
# 为什么这样：git 那条路每次整包重传（本机仓库上对象排除不生效），而产物 99% 的字节和上一版一样。
# 硬链接 + 只传变化的文件 ⇒ 内容没变就是 0 字节。
set -uo pipefail
ROOT=/srv/downloads/games
MODE="${1:-}"; SEG="${2:-}"; VER="${3:-}"; PREV="${4:-}"
[ -n "$MODE" ] && [ -n "$SEG" ] && [ -n "$VER" ] || { echo "用法: $0 <prepare|finalize> <产品段> <版本> [上一版]" >&2; exit 2; }
BASE="$ROOT/$SEG"
ARC="$ROOT/$SEG-archive"

case "$MODE" in
prepare)
  mkdir -p "$BASE/manifests"
  if [ -d "$BASE/$VER" ]; then
    echo "版本目录已存在（保留）：games/$SEG/$VER"
  elif [ -n "$PREV" ] && [ -d "$BASE/$PREV" ]; then
    cp -al "$BASE/$PREV" "$BASE/$VER"
    echo "已从上一版硬链接：games/$SEG/$PREV → games/$SEG/$VER（不额外占空间）"
  else
    mkdir -p "$BASE/$VER"
    echo "新建版本目录：games/$SEG/$VER"
  fi
  ;;
finalize)
  # 1) 归档上一版（硬链接，不额外占空间）。只有"上一版目录还在"时才重建归档 ——
  #    这样重跑 finalize 不会把已有归档删掉（2026-09-20 踩过：先删后建会丢归档）。
  if [ -n "$PREV" ] && [ -d "$BASE/$PREV" ]; then
    rm -rf "$ARC"; mkdir -p "$ARC"
    cp -al "$BASE/$PREV" "$ARC/$PREV"
    [ -f "$BASE/manifests/$PREV.json" ] && cp -al "$BASE/manifests/$PREV.json" "$ARC/$PREV.manifest.json"
    echo "已归档上一版：games/$SEG-archive/$PREV"
  fi

  # 2) 版本目录里只留清单里列的文件：
  #    从上一版硬链接整目录时，会把这一版已经删掉的文件（例如旧形态的整包 OBB）一起带过来，这里清掉。
  MF="$BASE/manifests/$VER.json"
  if [ -f "$MF" ]; then
    KEEP="/tmp/axtools-keep-$VER-$$.txt"
    grep -oE "\"path\"[[:space:]]*:[[:space:]]*\"[^\"]+\"" "$MF" |
      sed -E "s/.*:[[:space:]]*\"([^\"]+)\"/\1/" | sort -u > "$KEEP"
    ( cd "$BASE/$VER" && find . -type f | sed "s|^\./||" ) | while IFS= read -r rel; do
      if ! grep -qxF "$rel" "$KEEP"; then
        rm -f "$BASE/$VER/$rel"
        echo "已删除不在清单里的文件：games/$SEG/$VER/$rel"
      fi
    done
    find "$BASE/$VER" -mindepth 1 -type d -empty -delete 2>/dev/null
    rm -f "$KEEP"
  fi

  # 3) 树里只留当前版（和之前 git 那套的保留策略一致）
  for d in "$BASE"/*/; do
    [ -d "$d" ] || continue
    name="$(basename "$d")"
    [ "$name" = "manifests" ] && continue
    [ "$name" = "$VER" ] && continue
    rm -rf "$d"
    echo "已移除更早的版本：games/$SEG/$name"
  done
  for f in "$BASE/manifests"/*.json; do
    [ -f "$f" ] || continue
    name="$(basename "$f" .json)"
    [ "$name" = "$VER" ] && continue
    rm -f "$f"
    echo "已移除更早的清单：games/$SEG/manifests/$(basename "$f")"
  done

  # 4) latest.json：给启动器和 AxTools 读"线上最新版"（原子写）
  #    碎片清单若已随这一版上传（fragments/<版本>.json 存在），带上 fragmentsUrl；
  #    否则不写这个字段。这样碎片发布和游戏包发布互不覆盖。
  FRAG_FIELD=""
  if [ -f "$BASE/fragments/$VER.json" ]; then
    FRAG_FIELD=$(printf ',"fragmentsUrl":"https://www.crossingvoid.top/games/%s/fragments/%s.json"' "$SEG" "$VER")
  fi
  TMP="$BASE/latest.json.tmp.$$"
  printf "{\"schemaVersion\":1,\"productSegment\":\"%s\",\"version\":\"%s\",\"manifestUrl\":\"https://dl.crossingvoid.top/games/%s/manifests/%s.json\",\"generatedAt\":\"%s\"%s}\n" \
    "$SEG" "$VER" "$SEG" "$VER" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$FRAG_FIELD" > "$TMP"
  mv "$TMP" "$BASE/latest.json"
  echo "已更新：games/$SEG/latest.json → $VER"
  echo "体积：$([ -d "$BASE" ] && du -sh "$BASE" | cut -f1)  归档：$([ -d "$ARC" ] && du -sh "$ARC" | cut -f1 || echo 无)  磁盘剩余：$(df -h /srv | tail -1 | awk "{print \$4}")"
  ;;
*)
  echo "未知模式：$MODE" >&2; exit 2;;
esac
exit 0
