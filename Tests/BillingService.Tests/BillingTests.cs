using System;
using Xunit;

public class BillingTests
{
    [Fact]
    public void NewBudget_Starts_PendingApproval()
    {
        var budget = new Budget { OrderId = Guid.NewGuid(), Description = "Revisao", Amount = 150m };

        Assert.Equal(BudgetStatuses.PendingApproval, budget.Status);
        Assert.NotEmpty(budget.Id);
        Assert.Null(budget.PaymentProvider);
    }

    [Fact]
    public void BudgetStatuses_Contain_Approval_And_Payment_States()
    {
        Assert.Equal("PENDING_APPROVAL", BudgetStatuses.PendingApproval);
        Assert.Equal("APPROVED", BudgetStatuses.Approved);
        Assert.Equal("PAID", BudgetStatuses.Paid);
    }

    [Fact]
    public void Payment_Compensation_Does_Not_Reject_Paid_Or_Unapproved_Budget()
    {
        Assert.True(BudgetSaga.CanRejectPayment(BudgetStatuses.Approved));
        Assert.False(BudgetSaga.CanRejectPayment(BudgetStatuses.Paid));
        Assert.False(BudgetSaga.CanRejectPayment(BudgetStatuses.PendingApproval));
        Assert.False(BudgetSaga.CanRejectPayment(BudgetStatuses.PaymentRejected));
        Assert.True(BudgetSaga.CanApprovePayment(BudgetStatuses.PaymentRejected));
        Assert.False(BudgetSaga.CanApprovePayment(BudgetStatuses.Rejected));
        Assert.False(BudgetSaga.CanApprovePayment(BudgetStatuses.Paid));
    }

    [Fact]
    public void Outbox_Event_Keeps_Stable_Id_And_Payload_For_Retry()
    {
        var item = SagaOutboxEvent.Create("PAYMENT_APPROVED", new { orderId = Guid.Empty });
        Assert.Equal("PAYMENT_APPROVED", item.Type);
        Assert.Contains("PAYMENT_APPROVED", item.Body);
        Assert.Contains("orderId", item.Body);
        Assert.NotEmpty(item.Id);
    }
}
