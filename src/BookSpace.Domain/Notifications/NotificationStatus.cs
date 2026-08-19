namespace BookSpace.Domain.Notifications;

public enum NotificationType { BookingConfirmed, BookingRejected, BookingCancelled, Reminder, ApprovalRequested }

public enum NotificationStatus { Pending, Sent, Failed }
