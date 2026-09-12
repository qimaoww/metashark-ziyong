#!/usr/bin/env python3
import hashlib
import json
import sys
import re
import os
import subprocess
from datetime import datetime
from urllib.request import urlopen
from urllib.error import HTTPError


def get_repo_info():
    repo = os.environ.get('GITHUB_REPOSITORY', 'cxfksword/jellyfin-plugin-metashark')
    server = os.environ.get('GITHUB_SERVER_URL', 'https://github.com').rstrip('/')
    owner = repo.split('/')[0] if '/' in repo else repo
    return repo, owner, server


def generate_manifest(repo, owner, server):
    return    [{
        "guid": "9a19103f-16f7-4668-be54-9a1e7a4f7556",
        "name": "MetaShark",
        "description": "jellyfin电影元数据插件，影片信息只要从豆瓣获取，并由TMDB补充缺失的剧集数据。",
        "overview": "jellyfin电影元数据插件",
        "owner": owner,
        "category": "Metadata",
        "imageUrl": f"{server}/{repo}/raw/main/doc/logo.png",
        "versions": []
    }]

def generate_version(filepath, version, changelog, repo, server):
    return {
        'version': f"{version}.0",
        'changelog': changelog,
        'targetAbi': '12.0.0.0',
        'sourceUrl': f'{server}/{repo}/releases/download/v{version}/metashark_{version}.0.zip',
        'checksum': md5sum(filepath),
        'timestamp': datetime.now().strftime('%Y-%m-%dT%H:%M:%S')
    }

def md5sum(filename):
    with open(filename, 'rb') as f:
        return hashlib.md5(f.read()).hexdigest()


def main():
    filename = sys.argv[1]
    tag = sys.argv[2]
    version = tag.lstrip('v')
    filepath = os.path.join(os.getcwd(), filename)
    result = subprocess.run(['git', 'tag','-l','--format=%(contents)', tag, '-l'], stdout=subprocess.PIPE)
    changelog = result.stdout.decode('utf-8').strip()

    repo, owner, server = get_repo_info()

    # 解析旧 manifest
    try:
        manifest_url = f'{server}/{repo}/releases/download/manifest/manifest.json'
        with urlopen(manifest_url) as f:
            manifest = json.load(f)
    except HTTPError as err:
        if err.code == 404:
            manifest = generate_manifest(repo, owner, server)
        else:
            raise

    # 追加新版本/覆盖旧版本
    manifest[0]['versions'] = list(filter(lambda x: x['version'] != f"{version}.0", manifest[0]['versions']))
    manifest[0]['versions'].insert(0, generate_version(filepath, version, changelog, repo, server))

    with open('manifest.json', 'w') as f:
        json.dump(manifest, f, indent=2)

    # # 国内加速
    cn_domain = 'https://ghfast.top/'
    if 'CN_DOMAIN' in os.environ and os.environ["CN_DOMAIN"]:
        cn_domain = os.environ["CN_DOMAIN"]
    cn_domain = cn_domain.rstrip('/')
    with open('manifest_cn.json', 'w') as f:
        manifest_cn = json.dumps(manifest, indent=2)
        manifest_cn = re.sub('https://github.com', f'{cn_domain}/https://github.com', manifest_cn)
        f.write(manifest_cn)


if __name__ == '__main__':
    main()