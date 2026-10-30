# 공개 정보와 브랜드 출처

확인일: 2026-10-28. API 키가 필요한 OpenDART, data.go.kr 및 상용 뉴스 API를 사용하지 않습니다. 공개 접근 가능성이 무제한 사용·재배포 권리를 뜻하지는 않습니다. 제공자 정책과 웹 구조가 바뀌면 연결을 다시 검증합니다.

| 용도 | 소스 | 구현상 제한 |
|---|---|---|
| 공식 뉴스 검색/본문 | [LG Energy Solution Inside](https://inside.lgensol.com/), WordPress `/wp-json/wp/v2/posts` | 회사 발표로 표기; 본문 12,000자 이내 |
| 외부 뉴스 발견 | [GDELT DOC API](https://blog.gdeltproject.org/gdelt-doc-2-0-api-debuts/) | 제목·링크 검색; seen time은 발행일과 다를 수 있음; 부분 실패 허용 |
| 회사 개요 | [LG에너지솔루션 회사 소개](https://www.lgensol.com/kr/company/info-outline) | 공식 페이지 텍스트 |
| IR 자료 | [실적 발표](https://www.lgensol.com/kr/investors/earnings-announcement) | 연도별 PDF 링크; PDF 본문 추출 안 함 |
| 주가 | [공식 주가 페이지](https://www.lgensol.com/kr/investors/stock-graph-recent)에 포함된 [GSIFN](https://ir.gsifn.io/lgensol/ir_current.html?koreng=1) | HTML 파싱; 지연 미지정; 기준 시각과 오래된 자료 여부 표시 |
| CI | [공식 CI](https://www.lgensol.com/en/company/info-ci) | 심벌과 워드마크의 비율 유지, 공식 원본 사용 |

MCP 서버는 허용된 HTTPS 호스트만 요청하며 redirect, 임의 URL, 비표준 포트, 사용자 정보 포함 URL을 거부합니다. 응답 크기는 2 MiB, 요청 시간은 10초로 제한합니다. 캐시는 주가 60초, 뉴스 5분, 본문 10분, 공식 회사/IR 페이지 30분입니다.

외부 기사 내용·사용자 메시지·토큰을 통상 애플리케이션 로그에 남기지 않습니다. 외부 공급자에 검색어가 전송되므로 공개 회사 정보에 관한 질문으로 시연합니다. 고객 기밀 자료나 개인정보를 검색어로 사용하지 않습니다.
