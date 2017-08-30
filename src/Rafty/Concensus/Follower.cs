using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using Rafty.FiniteStateMachine;
using Rafty.Log;

namespace Rafty.Concensus
{
    public sealed class Follower : IState
    {
        private readonly IFiniteStateMachine _fsm;
        private readonly ILog _log;
        private readonly IRandomDelay _random;
        private Timer _electionTimer;
        private int _messagesSinceLastElectionExpiry;
        private readonly INode _node;
        private ISettings _settings;

        public Follower(CurrentState state, IFiniteStateMachine stateMachine, ILog log, IRandomDelay random, INode node, ISettings settings)
        {
            _random = random;
            _node = node;
            _settings = settings;
            _fsm = stateMachine;
            CurrentState = state;
            _log = log;
            ResetElectionTimer();
        }

        private void ElectionTimerExpired()
        {
            if (_messagesSinceLastElectionExpiry == 0)
            {
                _node.BecomeCandidate(CurrentState);
            }
            else
            {
                _messagesSinceLastElectionExpiry = 0;
                ResetElectionTimer();
            }
        }

        private void ResetElectionTimer()
        {
            var timeout = _random.Get(_settings.MinTimeout, _settings.MaxTimeout);
            _electionTimer?.Dispose();
            _electionTimer = new Timer(x =>
            {
                ElectionTimerExpired();

            }, null, Convert.ToInt32(timeout.TotalMilliseconds), Convert.ToInt32(timeout.TotalMilliseconds));
        }

        public CurrentState CurrentState { get; private set;}


        public AppendEntriesResponse Handle(AppendEntries appendEntries)
        {
            //Reply false if term < currentTerm (§5.1)
            if (appendEntries.Term < CurrentState.CurrentTerm)
            { 
                if(appendEntries.Entries.Count > 0)
                {
                    Console.WriteLine("Follower voting false because AE term less than current term");
                }
                return new AppendEntriesResponse(CurrentState.CurrentTerm, false);
            }

            // Reply false if log doesn’t contain an entry at prevLogIndex whose term matches prevLogTerm (§5.3)
            var termAtPreviousLogIndex = _log.GetTermAtIndex(appendEntries.PreviousLogIndex);
            if (termAtPreviousLogIndex > 0 && termAtPreviousLogIndex != appendEntries.PreviousLogTerm)
            {
                if(appendEntries.Entries.Count > 0)
                {
                    Console.WriteLine("Follower voting false because terms at previous log index dont match");
                }
                return new AppendEntriesResponse(CurrentState.CurrentTerm, false);
            }

            //If an existing entry conflicts with a new one (same index but different terms), delete the existing entry and all that follow it(§5.3)
             var count = 1;
            foreach (var newLog in appendEntries.Entries)
            {
                _log.DeleteConflictsFromThisLog(appendEntries.PreviousLogIndex + 1, newLog);
                count++;
            }

            //Append any new entries not already in the log
            foreach (var log in appendEntries.Entries)
            {
                _log.Apply(log);
            }

            CurrentState nextState = CurrentState;
            //todo consolidate with request vote
            if (appendEntries.Term > CurrentState.CurrentTerm)
            {
                nextState = new CurrentState(CurrentState.Id, appendEntries.Term,
                    CurrentState.VotedFor, CurrentState.CommitIndex, CurrentState.LastApplied);
            }

            //If leaderCommit > commitIndex, set commitIndex = min(leaderCommit, index of last new entry)
            var commitIndex = CurrentState.CommitIndex;
            var lastApplied = CurrentState.LastApplied;
            if (appendEntries.LeaderCommitIndex > CurrentState.CommitIndex)
            {
                //This only works because of the code in the node class that handles the message first (I think..im a bit stupid)
                var lastNewEntry = _log.LastLogIndex;
                commitIndex = System.Math.Min(appendEntries.LeaderCommitIndex, lastNewEntry);
            }

            //If commitIndex > lastApplied: increment lastApplied, apply log[lastApplied] to state machine (§5.3)\
            //todo - not sure if this should be an if or a while
            while (commitIndex > lastApplied)
            {
                lastApplied++;
                var log = _log.Get(lastApplied);
                //todo - json deserialise into type? Also command might need to have type as a string not Type as this
                //will get passed over teh wire? Not sure atm ;)
                _fsm.Handle(log.CommandData);
            }

            CurrentState = new CurrentState(CurrentState.Id, nextState.CurrentTerm,
                CurrentState.VotedFor, commitIndex, lastApplied);

            _messagesSinceLastElectionExpiry++;
            
            return new AppendEntriesResponse(CurrentState.CurrentTerm, true);
        }

        private (RequestVoteResponse requestVoteResponse, bool shouldReturn) RequestVoteTermIsGreaterThanCurrentTerm(RequestVote requestVote)
        {
            var term = CurrentState.CurrentTerm;

            if (requestVote.Term > CurrentState.CurrentTerm)
            {
                term = requestVote.Term;
                CurrentState = new CurrentState(CurrentState.Id, term, requestVote.CandidateId,
                    CurrentState.CommitIndex, CurrentState.LastApplied);
                 return (new RequestVoteResponse(true, CurrentState.CurrentTerm), true);
            }

            return (null, false);
        }

        private (RequestVoteResponse requestVoteResponse, bool shouldReturn) RequestVoteTermIsLessThanCurrentTerm(RequestVote requestVote)
        {
            //Reply false if term<currentTerm
            if (requestVote.Term < CurrentState.CurrentTerm)
            {
                return (new RequestVoteResponse(false, CurrentState.CurrentTerm), false);
            }

            return (null, false);
        }

        private (RequestVoteResponse requestVoteResponse, bool shouldReturn) VotedForIsNotThisOrNobody(RequestVote requestVote)
        {
            if (CurrentState.VotedFor == CurrentState.Id || CurrentState.VotedFor != default(Guid))
            {
                return (new RequestVoteResponse(false, CurrentState.CurrentTerm), true);
            }

            return (null, false);
        }

        private (RequestVoteResponse requestVoteResponse, bool shouldReturn) LastLogIndexAndLastLogTermMatchesThis(RequestVote requestVote)
        {
             if (requestVote.LastLogIndex == _log.LastLogIndex &&
                requestVote.LastLogTerm == _log.LastLogTerm)
            {
                // update voted for....
                CurrentState = new CurrentState(CurrentState.Id, CurrentState.CurrentTerm, requestVote.CandidateId, CurrentState.CommitIndex, CurrentState.LastApplied);

                _messagesSinceLastElectionExpiry++;
                return (new RequestVoteResponse(true, CurrentState.CurrentTerm), true);
            }

            return (null, false);
        }


        public RequestVoteResponse Handle(RequestVote requestVote)
        {
            var response = RequestVoteTermIsGreaterThanCurrentTerm(requestVote);

            if(response.shouldReturn)
            {
                return response.requestVoteResponse;
            }

            response = RequestVoteTermIsLessThanCurrentTerm(requestVote);

            if(response.shouldReturn)
            {
                return response.requestVoteResponse;
            }

            response = VotedForIsNotThisOrNobody(requestVote);

            if(response.shouldReturn)
            {
                return response.requestVoteResponse;
            }

            response = LastLogIndexAndLastLogTermMatchesThis(requestVote);

            if(response.shouldReturn)
            {
                return response.requestVoteResponse;
            }

            return new RequestVoteResponse(false, CurrentState.CurrentTerm);
        }

        public Response<T> Accept<T>(T command)
        {
            //todo - send message to leader...
            throw new NotImplementedException();
        }

        public void Stop()
        {
            _electionTimer.Dispose();
        }
    }
}