# 배포 파일과 실행 순서

복사해서 실행할 전체 절차는 [한국어 README](../README.md)의 4–11단계에 있습니다. 이 문서는 단계별 입력/출력과 변경 범위를 정리합니다. 원래 데모의 테넌트·구독·실제 ID는 공개하지 않습니다.

| 순서 | 실행 파일/명령 | 입력 | 생성/변경 |
|---|---|---|---|
| 1 | 환경 설정 | `lab.settings.example.json` | `.local/lab.settings.json` |
| 2 | `a365 setup all` (1.1.221, OBO) | 테넌트/관리자 동의 | `.local/onboarding` 설정, Blueprint/child identity |
| 3 | `infra/foundation.bicep` | 기존 ACR, 그룹/지역 | 관리 ID 2개, AcrPull; 출력 `.local/foundation.json` |
| 4 | `tools/Initialize-Tenant.ps1` | 온보딩 및 foundation 출력 | MCP/채널 앱, scope, 인증 연합, DPAPI 비밀, `.local/deployment.json` |
| 5 | `a365 setup permissions custom` | 초기화가 작성한 customBlueprintPermissions | 권한 동의 |
| 6 | `tools/Set-RuntimePermissions.ps1` | 로컬 설정/배포 상태 | 해당 Blueprint 동의 조정, child identity Foundry RBAC |
| 7 | `Set-LgesPurviewDlpPolicy.ps1` | 명시적인 테넌트/Blueprint/정책/관리자 | 기본 조회; `-Apply` 시 기존 정책 대상 추가 |
| 8 | `az acr build` | Dockerfile 두 개, 새 release tag | host/mcp 이미지 |
| 9 | `tools/New-DeploymentParameters.ps1 -ReleaseTag ...` | ACR digest, 설정, foundation/신원 상태 | `.local/main.parameters.json` |
| 10 | `infra/main.bicep` | 위 매개변수 | 공개 호스트 1개, 내부 MCP 3개; `.local/app-outputs.json` |
| 11 | `tools/Deploy-Bot.ps1` | 배포 출력, DPAPI 비밀 | Azure Bot, Teams 채널, OAuth 연결 |
| 12 | `a365 setup blueprint --endpoint-only` | 배포된 `/api/messages/obo` URL | Blueprint 메시지 엔드포인트 |
| 13 | `tools/New-TeamsPackage.ps1` | 배포 상태, manifest, 아이콘 | `artifacts/packages`의 ZIP |
| 14 | 관리 센터 업로드/사용자 제공/설치 | ZIP, 관리자 권한 | 카탈로그·사용자 채널 사용 설정 |

## 경계와 주의점

- ACR/Container Apps Environment/Foundry 모델/Content Safety/로그 환경은 이 Bicep이 만들지 않습니다. 기존 리소스 요구사항은 README 표를 따릅니다.
- 모든 Azure 명령에 본인의 구독과 그룹을 사용합니다. 증분 배포와 what-if로 변경을 확인합니다. 이번 문서 정리 작업에서는 클라우드를 새로 배포하지 않았습니다.
- 스크립트는 하나의 프로젝트/테넌트 상태를 전제로 합니다. 다른 실습의 `.local`을 복사하거나 같은 이름의 앱을 무작정 재생성하지 마세요.
- `Set-RuntimePermissions.ps1`은 사용자/그룹 RBAC, 별도 Content Safety 계정 권한, 라이선스를 자동 구성하지 않습니다.
- Bot OAuth 자격 증명은 6개월 만료입니다. 새 비밀 발급과 연결 업데이트를 운영 절차에 포함하세요.
- 패키지 업데이트는 `Update-TeamsPackage.ps1`로 버전을 올립니다. 서버 코드 수정은 이미지 재빌드/배포가 필요합니다. ZIP 업로드만으로 서버 코드가 바뀌지 않습니다.
- 원래 데모의 `a365 publish` 대체 경로와 CLI 2.x 마이그레이션은 검증하지 않았습니다.

## 문제 발생 시

직전 정상 revision이 있으면 그 이미지/설정으로 재배포합니다. 새 환경에서는 원인을 해결한 뒤 재시도하며 공유 그룹 전체를 삭제하지 않습니다. `.local` 상태는 중복 신원 생성을 피하기 위해 안전하게 보존합니다. Git에는 추가하지 마세요.

참고: [Agent 365 setup](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/setup), [CLI 변경 이력](https://github.com/microsoft/Agent365-devTools/blob/main/CHANGELOG.md), [OBO 참조 runbook](https://github.com/microsoft/agent365-runbook/blob/main/01-scenarios/Teams-Agent-Custom-Engine-OBO/3.Runbook-Manual.md).
