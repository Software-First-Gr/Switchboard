namespace Switchboard;

/// <summary>Combined <see cref="ISender"/> and <see cref="IPublisher"/>.</summary>
public interface IMediator : ISender, IPublisher;
