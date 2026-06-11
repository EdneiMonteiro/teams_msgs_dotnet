# Aviso Legal (Disclaimer)

Este repositório contém código de exemplo, demonstrações e/ou provas de conceito fornecidos exclusivamente para fins ilustrativos, educacionais e experimentais.

## Não destinado a produção

Este código **não foi desenvolvido para uso direto em produção**.

Ele pode não atender requisitos essenciais de um ambiente produtivo, como:

- Segurança
- Alta disponibilidade
- Recuperação de desastres
- Monitoramento
- Logging adequado
- Performance
- Conformidade regulatória

## Trade-offs específicos desta PoC (.NET / AKS / Storage Queue)

Foram feitas escolhas explicitamente PoC-friendly que **não** devem ser replicadas sem revisão em produção:

- `Storage Queue` em vez de `Service Bus`: limite de **64 KB por mensagem** (AdaptiveCards grandes não cabem). Sem dedup nativa — supressão de duplicação foi implementada na aplicação (`sentmarks`). Sem DLQ nativa — `send-messages-poison` é manual após `DequeueCount > 5`.
- `Table Storage` no lugar de `Redis` para contadores: atualizações atômicas via `ETag`/`If-Match` + retry exponencial (`Polly`). Sob alta concorrência (>500 increments concorrentes no mesmo `jobId`), o tempo de resposta cresce.
- `Istio EnvoyFilter local_ratelimit` no lugar do token bucket Lua do Redis: limite é **por pod**, não global cross-pod.
- AKS com SKU Base/Free no control plane e `Standard_D2s_v5` em 2 nodes: sem HA real, sem multi-AZ.
- Ingress via `LoadBalancer` exposto publicamente sem WAF/DDoS premium.

Para uso real, revise cada um desses pontos.

## Sem Garantias

TODO O CÓDIGO É FORNECIDO **"NO ESTADO EM QUE SE ENCONTRA"**, SEM GARANTIAS DE QUALQUER TIPO, EXPRESSAS OU IMPLÍCITAS, INCLUINDO, MAS NÃO SE LIMITANDO A:

- Comercialização
- Adequação a um propósito específico
- Não violação de direitos

## Sem Suporte Oficial da Microsoft

Este projeto:

- Não é afiliado à Microsoft
- Não é endossado pela Microsoft
- Não possui suporte oficial da Microsoft

## Sem compromisso de suporte

O autor/mantenedor não se compromete a:

- Corrigir bugs
- Atualizar o projeto
- Fornecer suporte
- Manter compatibilidade futura

Qualquer melhoria será feita **quando e se houver disponibilidade**.

## Uso por sua conta e risco

Ao utilizar este código, você concorda que:

- É responsável por validar o funcionamento
- Deve testar em ambiente não produtivo
- Assume todos os riscos associados

O autor não se responsabiliza por:

- Perda de dados
- Interrupção de serviços
- Falhas operacionais
- Impactos financeiros

## Dependências e mudanças externas

Este código pode depender de APIs e serviços que podem mudar sem aviso prévio, o que pode causar:

- Quebra de funcionalidade
- Comportamentos inesperados

## Responsabilidade do usuário

Antes de utilizar, você deve:

- Entender completamente o que o código faz
- Revisar permissões e acessos
- Validar impacto em dados
- Garantir que há rollback possível

## Licença

O uso deste código também segue os termos definidos no arquivo LICENSE.

## Uso em ambiente corporativo

Caso este código seja utilizado em ambiente corporativo ou com clientes, recomenda-se:

- Revisão de arquitetura
- Avaliação de segurança
- Validação de compliance
