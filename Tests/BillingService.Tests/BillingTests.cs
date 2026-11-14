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
}
