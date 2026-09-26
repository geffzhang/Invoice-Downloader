using FluentAssertions;
using InvoiceFlowAI.Application.Mail;
using Xunit;

namespace InvoiceFlowAI.Application.Tests.Mail;

public sealed class EmailTierClassifierTests
{
    [Fact]
    public void Known_sender_domain_from_12306_cn_is_tier_one()
    {
        var classifier = new EmailTierClassifier();

        classifier.Classify("China Railway 12306 <service@12306.cn>", "Hello", "Plain body").Should().Be(1);
    }

    [Fact]
    public void Subdomain_of_12306_cn_does_not_match_tier_one()
    {
        var classifier = new EmailTierClassifier();

        classifier.Classify("China Railway 12306 <service@sub.12306.cn>", "Hello", "Plain body").Should().Be(4);
    }

    [Fact]
    public void Sender_domain_uses_the_last_at_symbol_and_ignores_evil_suffixes()
    {
        var classifier = new EmailTierClassifier();

        classifier.Classify("China Railway 12306 <service@12306.cn@evil.com>", "Hello", "Plain body").Should().Be(4);
    }

    [Fact]
    public void Mixed_case_invoice_subject_is_tier_two()
    {
        var classifier = new EmailTierClassifier();

        classifier.Classify("alice@example.com", "InVoIcE for your trip", "Plain body").Should().Be(2);
    }

    [Fact]
    public void Chinese_invoice_keyword_in_body_is_tier_three()
    {
        var classifier = new EmailTierClassifier();

        classifier.Classify("alice@example.com", "Hello", "请查收发票并报销").Should().Be(3);
    }

    [Fact]
    public void Unrelated_message_falls_back_to_tier_four()
    {
        var classifier = new EmailTierClassifier();

        classifier.Classify("alice@example.com", "Weekend plans", "See you soon").Should().Be(4);
    }
}