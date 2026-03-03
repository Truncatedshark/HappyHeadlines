namespace CommentService.Models;

public enum CommentStatus
{
    Pending,   // ProfanityService was unavailable — awaiting re-check
    Approved,  // Passed profanity check
    Rejected   // Contains profanity
}
