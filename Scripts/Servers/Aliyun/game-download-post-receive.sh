#!/bin/bash
# 游戏下载站（野草云 /srv/downloads）的 post-receive hook —— 所有 game-*.git 共用同一份。
#
# 干什么：
#   1) 落位前，把工作树里**本产品**现有的版本目录硬链接归档到 games/<产品>-archive/<版本>/
#      （硬链接，归档瞬间不复制数据；随后 checkout 会删掉树里的原件，最终磁盘上只有一份）
#   2) checkout -f main 把推送的内容落到 /srv/downloads（对外下载目录）
#   3) 先过期 reflog，再做一次"只打包可达对象"的 repack + prune，回收被替换掉的旧对象。
#      三点都不能省（2026-09-20 实测踩出来的）：
#      · 默认 gc 只清"90 天前的 reflog"，而 force-push 换掉的旧提交正是靠 reflog 吊着的；
#      · `git gc` 走的是 `repack -A`，它会把不可达对象**留住**，所以这里用 `repack -a -d`：
#        `-a`（不带大写 A）只打包可达对象，配合 prune 才能真正回收；
#      · 不能加 `pack.packSizeLimit`：拆包会把同一份大对象重复写进不同包，
#        实测 2.5 GB 的内容被撑到 3.7 GB。
#      这台 VPS 只有 2 GB 内存且无 swap，所以仍然限制窗口内存并单线程
#      （不加限制会被 OOM 杀掉，实测 pack-objects died of signal 9）。
#   4) 把仓库对象体积 / 工作树体积 / 磁盘余量打印给客户端（推送时就能看到）
#
# 部署（换服务器或重建仓库时用；两条命令即可）：
#   scp yecaoyun-hk:/srv/repos/game-crossingvoid.git/hooks/post-receive ...
#   或： for r in /srv/repos/game-*.git; do
#          cp game-download-post-receive.sh "$r/hooks/post-receive"; chmod +x "$r/hooks/post-receive";
#          chown uploader:uploader "$r/hooks/post-receive"; done
#
# 保留策略（用户 2026-09-20 明确）：只留「最新的一版 + 它的上一版」各一个。
# 树里只放最新版；上一版在 *-archive/ 里；更早的都被这一步覆盖掉。

set -uo pipefail
REPO="$(pwd)"
NAME="$(basename "$REPO" .git)"
PRODUCT="${NAME#game-}"      # game-crossingvoid -> crossingvoid；game-crossingvoid-android -> crossingvoid-android
WORK=/srv/downloads
LOG="/srv/repos/${NAME}.hook.log"

# 关键设置：让 git 不要把 ctime 当成"文件变脏"的依据。
# 第 1 步的硬链接会刷新每个文件的 ctime，默认设置下 checkout -f 会认为 87 个文件
# 全被改过、逐个重写一遍，硬链接当场全断（实测工作树凭空多出 2.6 GB）。
git --git-dir="$REPO" config core.trustctime false

archive_old_versions() {
  for dir in "$WORK"/games/"$PRODUCT"/; do
    [ -d "$dir" ] || continue
    arc="$WORK/games/${PRODUCT}-archive"
    rm -rf "$arc"
    found=0
    for vdir in "$dir"*/; do
      [ -d "$vdir" ] || continue
      ver="$(basename "$vdir")"
      [ "$ver" = "manifests" ] && continue          # 清单目录不是版本
      mkdir -p "$arc"
      cp -al "$vdir" "$arc/$ver"
      mf="$WORK/games/$PRODUCT/manifests/$ver.json"
      if [ -f "$mf" ]; then cp -al "$mf" "$arc/$ver.manifest.json"; fi
      found=$((found+1))
      echo "  归档上一版：games/${PRODUCT}-archive/$ver"
    done
    [ "$found" = "0" ] && rmdir "$arc" 2>/dev/null
    true
  done
}

report_sizes() {
  echo "  仓库对象：$(du -sh "$REPO" 2>/dev/null | cut -f1)   工作树：$(du -sh "$WORK/games" 2>/dev/null | cut -f1)"
  echo "  磁盘剩余：$(df -h / | awk 'NR==2{print $4}')"
}

{
  echo "--- $(date -Is) [$NAME] 收到推送 ---"
  while read -r old new ref; do echo "  $ref  ${old:0:8} -> ${new:0:8}"; done
  archive_old_versions
  if git --git-dir="$REPO" --work-tree="$WORK" checkout -f main; then
    echo "  落位完成：$(git --git-dir="$REPO" --work-tree="$WORK" rev-parse --short main)"
    # 刷新 index 里的 stat 缓存：否则下一次 checkout -f 会认为每个文件都"脏了"、
    # 逐个大文件重写一遍，把第 1 步做的硬链接全部打断（实测工作树凭空多出 2.5 GB）。
    git --git-dir="$REPO" --work-tree="$WORK" update-index --refresh -q >/dev/null 2>&1 || true
  else
    echo "  ！！落位失败（见上面的 error）—— 下载站可能还是旧内容，请检查权限"
  fi
  echo "  正在回收旧对象（省内存模式）…"
  if git --git-dir="$REPO" reflog expire --expire=now --expire-unreachable=now --all >/dev/null 2>&1 &&
     git --git-dir="$REPO" -c pack.windowMemory=128m -c pack.threads=1 repack -a -d >/dev/null 2>&1 &&
     git --git-dir="$REPO" prune --expire=now >/dev/null 2>&1; then
    echo "  对象回收完成"
  else
    echo "  ！！对象回收失败（不影响本次落位）"
  fi
  report_sizes
} 2>&1 | tee -a "$LOG" >&2
