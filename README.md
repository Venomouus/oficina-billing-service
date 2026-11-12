# Oficina Billing Service

Microsservico responsavel por orcamentos e pelo fluxo inicial de pagamento.

## Stack

- ASP.NET Core 8
- MongoDB exclusivo do servico
- RabbitMQ para integracao assincrona
- Swagger/OpenAPI

## Eventos

- Consome `OS_CREATED` e gera um orcamento.
- Publica `BUDGET_CREATED`.
- Publica `BUDGET_APPROVED` apos aprovacao.
- Publica `BUDGET_REJECTED` quando o cliente recusa.
- Publica `PAYMENT_APPROVED` apos confirmacao do pagamento.

O pagamento atual usa um fluxo de demonstracao. A integracao com o Mercado Pago sera adicionada mantendo estes eventos.

## Endpoints principais

- `GET /api/budgets`
- `GET /api/budgets/{id}`
- `POST /api/budgets/{id}/approve`
- `POST /api/budgets/{id}/reject`
- `POST /api/budgets/{id}/payment`

## Executar

```powershell
dotnet run
```

Configuracao padrao: MongoDB em `localhost:27017` e RabbitMQ em `localhost:5672`.
