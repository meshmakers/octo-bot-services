using System.ComponentModel;
using Hangfire;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Services;
using NLog;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     Hangfire Job to send e-mails for notification messages
/// </summary>
public class EMailSenderJob : IEMailSenderJob
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IEMailSender _eMailSender;
    private readonly INotificationRepository _notificationRepository;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="notificationRepository">The notification message repository</param>
    /// <param name="eMailSender">E-Mail sender for SMS</param>
    public EMailSenderJob(INotificationRepository notificationRepository, IEMailSender eMailSender)
    {
        _notificationRepository = notificationRepository;
        _eMailSender = eMailSender;
    }

    /// <summary>
    ///     Exports a runtime model
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="cancellationToken">An cancellation token to abort the job</param>
    /// <returns>The key the result file is stored.</returns>
    [DisplayName("E-Mail Sender '{0}'")]
    [DisableConcurrentExecution(10 * 60)]
    public async Task SendEMail(string tenantId, IBotCancellationToken? cancellationToken)
    {
        Logger.Info($"Job to send E-Mails started for tenant '{tenantId}'");

        PagedResult<NotificationMessageDto> pagedResult;
        do
        {
            pagedResult =
                await _notificationRepository.GetPendingMessagesAsync(tenantId, NotificationTypesDto.EMail, 20);
            if (pagedResult.TotalCount == 0)
            {
                break;
            }

            Logger.Info($"Handling of '{pagedResult.List.Count}' notification messages at tenant '{tenantId}'");

            foreach (var notificationMessageDto in pagedResult.List)
            {
                notificationMessageDto.LastTryDateTime = DateTime.UtcNow;
            }

            await _notificationRepository.StoreNotificationMessages(tenantId, pagedResult.List);

            Logger.Info($"LastTryDateTime updated for tenant '{tenantId}'");
            cancellationToken?.ThrowIfCancellationRequested();
            Logger.Info($"Start sending E-Mail '{tenantId}'");
            foreach (var notificationMessageDto in pagedResult.List)
            {
                if (string.IsNullOrWhiteSpace(notificationMessageDto.RecipientAddress))
                {
                    continue;
                }

                try
                {
                    await _eMailSender.SendEmailAsync(notificationMessageDto.RecipientAddress,
                        notificationMessageDto.SubjectText,
                        notificationMessageDto.BodyText);

                    notificationMessageDto.SentDateTime = DateTime.UtcNow;
                    notificationMessageDto.SendStatus = SendStatusDto.Sent;
                    notificationMessageDto.ErrorText = null;
                }
                catch (NotificationSendFailedException e)
                {
                    Logger.Error(e, "Sending of E-Mail failed.");

                    notificationMessageDto.SentDateTime = DateTime.UtcNow;
                    notificationMessageDto.SendStatus = SendStatusDto.Error;
                    notificationMessageDto.ErrorText = e.GetDirectAndIndirectMessages();
                }
            }

            Logger.Info($"Updating sent date '{tenantId}'");
            await _notificationRepository.StoreNotificationMessages(tenantId, pagedResult.List);
            Logger.Info($"Completed updating sent date for E-Mail notfications '{tenantId}'. Next page.");
        } while (pagedResult.TotalCount > 0);
    }
}