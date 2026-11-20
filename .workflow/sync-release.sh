#!/usr/bin/env bash
# 将 GitHub 最新 Release 同步到 Gitee：tag、标题、描述、附件保持一致。
# 幂等：Gitee 已存在同 tag 的 Release 时直接跳过。
#
# 需要在流水线参数中配置：
#   GITEE_TOKEN  Gitee 私人令牌（projects 权限，设为私密参数）
#   GITEE_REPO   Gitee 仓库路径，如 yourname/SETBoard（默认 RejTech/SETBoard）
set -euo pipefail

GITHUB_REPO="${GITHUB_REPO:-RejTech/SETBoard}"
GITEE_REPO="${GITEE_REPO:-RejTech/SETBoard}"
: "${GITEE_TOKEN:?请在流水线参数中配置 GITEE_TOKEN（Gitee 私人令牌）}"

API="https://gitee.com/api/v5/repos/$GITEE_REPO"

echo "==> 读取 GitHub 最新 Release: $GITHUB_REPO"
release=$(curl -fsSL "https://api.github.com/repos/$GITHUB_REPO/releases/latest")
tag=$(jq -r '.tag_name' <<<"$release")
name=$(jq -r '.name // .tag_name' <<<"$release")
prerelease=$(jq -r '.prerelease' <<<"$release")
echo "    $tag  $name"

if curl -fsSL "$API/releases/tags/$tag?access_token=$GITEE_TOKEN" >/dev/null 2>&1; then
  echo "==> Gitee 已存在 $tag，无需同步"
  exit 0
fi

# 默认分支作为 target_commitish，tag 不存在时 Gitee 会基于它创建
default_branch=$(curl -fsSL "$API?access_token=$GITEE_TOKEN" | jq -r '.default_branch // "master"')

echo "==> 创建 Gitee Release（分支 $default_branch）"
jq -n --arg token "$GITEE_TOKEN" --arg tag "$tag" --arg name "$name" \
      --arg body "$(jq -r '.body // ""' <<<"$release")" \
      --arg branch "$default_branch" --argjson pre "$prerelease" \
  '{access_token:$token, tag_name:$tag, name:$name, body:$body,
    prerelease:$pre, target_commitish:$branch}' \
| curl -fsSL -X POST "$API/releases" -H 'Content-Type: application/json' -d @- > created.json
release_id=$(jq -r '.id' created.json)

echo "==> 同步附件"
mkdir -p release-assets
jq -c '.assets[]' <<<"$release" | while read -r asset; do
  url=$(jq -r '.browser_download_url' <<<"$asset")
  file=$(jq -r '.name' <<<"$asset")
  echo "    下载 $file"
  curl -fsSL "$url" -o "release-assets/$file"
  echo "    上传 $file"
  curl -fsSL -X POST "$API/releases/$release_id/attach_files" \
    -F "access_token=$GITEE_TOKEN" -F "release_id=$release_id" \
    -F "file=@release-assets/$file" >/dev/null
done

echo "==> 完成：Gitee $tag 已发布，信息及附件与 GitHub 一致"
