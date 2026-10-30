# 게시자·Owner·Sponsor와 각종 ID

관리 화면, 앱 패키지, 실행 신원은 서로 다른 정보를 보관합니다. 같은 이름의 항목이 두 개 보여도 서버가 두 번 배포되었다고 단정하지 않습니다. 실제 ID와 객체 유형을 대조하세요.

| 항목 | 이 프로젝트에서의 의미 |
|---|---|
| 게시자 | manifest의 `developer.name`은 데모 표시 이름 `LGES AMA Demo`. Entra 권한이나 회사 공식 인증을 뜻하지 않음 |
| Owner | 신원의 기술 관리 관계. 고객의 실제 관리 책임자를 확인 |
| Sponsor | 에이전트의 업무 목적과 수명주기 책임 관계. API 권한 자동 부여와 구분 |
| Blueprint ID | 신원 설정·상속의 기준. 이 데모의 Purview application location |
| child Agent Identity | 실제 에이전트 실행 신원. Foundry 역할 할당 대상 |
| channelAppId | Azure Bot/Teams SSO/OBO 채널 앱. Blueprint ID와 다름 |
| teamsAppId | manifest의 앱 ID. 버전 업데이트 때 유지 |
| teamsCatalogId | 조직 카탈로그가 부여한 ID. 기존 앱 업데이트 조회에 사용 |
| mcpAppId | 내부 MCP API audience와 `Mcp.Invoke` scope의 기준 |

실행 지침은 `server/agent/Lges.Ama.Agent/CompanyAgentInstructions.cs`에서 관리합니다. 포털의 지침 필드가 비어 있어도 모델에 지침이 전달되지 않는다고 판단할 수 없습니다. 포털 메타데이터를 자동 동기화하는 구현은 없습니다.

이름·설명·게시자·패키지 아이콘은 manifest 수정과 버전 업데이트로 제공할 수 있습니다. 모델 지침이나 실행 동작은 C# 수정 후 이미지 재배포가 필요합니다. 다른 등록 항목의 아이콘까지 같은 ZIP이 변경한다고 가정하지 마세요.

테넌트에서 Entra **Owners and sponsors**, Azure **Access control (IAM)**, 앱 카탈로그의 사용자 제공 범위를 각각 확인합니다. 신원 관리 관계, Azure RBAC, Graph 동의, 채널 사용 정책을 따로 설명하는 것이 좋습니다.

공식 자료: [Owner/Sponsor 개념](https://learn.microsoft.com/entra/agent-id/agent-owners-sponsors-managers), [관계 관리](https://learn.microsoft.com/entra/agent-id/manage-owners-sponsors-agents), [Agent registry](https://learn.microsoft.com/microsoft-agent-365/admin/agent-registry-overview).
